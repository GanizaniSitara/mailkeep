using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>The archive as the reader sees it (docs/folder-store-redesign.md section 12): a list of archive
/// locations, each a directory somewhere (another drive, say) holding archived folders, attached or detached as a
/// whole. Nobody handles individual files; the folder files inside a location are found by looking. Archived mail
/// is read-only here and never reaches the mail database: its messages carry negative ids (slot * Stride + seq,
/// negated). Message bytes are copied into a reading cache so reading, attachments and print work on a plain
/// file as they do for stored mail. The list of locations lives in archives.json beside the store.</summary>
public sealed class ArchiveStore
{
    public const string Source = "archive-file";
    private const long Stride = 1_000_000_000L;
    private readonly AppPaths _paths;
    private List<Archive> _archives;
    // Slots are handed out per folder file as it is read and never reused, so ids already on screen stay valid.
    private readonly List<(string File, string Uuid)> _slots = [];

    public ArchiveStore(AppPaths paths)
    {
        _paths = paths;
        _archives = Load();
    }

    /// <summary>A location and, within it, the folders detached one at a time (by label). A detached folder is
    /// not opened, listed or searched; its file stays where it is.</summary>
    public sealed record Archive(string Name, string Location, bool Attached, string[]? DetachedFolders = null)
    {
        public bool IsFolderDetached(string label)
            => DetachedFolders?.Contains(label, StringComparer.OrdinalIgnoreCase) == true;
    }
    public sealed record Folder(Archive Archive, string Label, string File, int Count, int Unread, bool Attached = true);
    public sealed record SearchResult(IReadOnlyList<MessageSummary> Hits, IReadOnlyList<string> NotSearched);

    public string RegistryPath => System.IO.Path.Combine(_paths.Root, "archives.json");

    public IReadOnlyList<Archive> Archives => _archives;

    /// <summary>Reads the list again, after something else (a folder being archived) has changed it.</summary>
    public void Reload() => _archives = Load();

    public static bool Owns(long id) => id < 0;

    public static bool IsAvailable(Archive archive) => Directory.Exists(archive.Location);

    /// <summary>Adds a location and attaches it. Adding one already listed just attaches it again.</summary>
    public Archive Add(string location, string? name = null)
    {
        var full = System.IO.Path.GetFullPath(location);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"{full} does not exist.");
        var existing = Find(full);
        var archive = new Archive(string.IsNullOrWhiteSpace(name) ? existing?.Name ?? DefaultName(full) : name.Trim(), full, true,
            existing?.DetachedFolders);
        _archives = [.. _archives.Where(item => item != existing), archive];
        Save();
        return archive;
    }

    public void SetAttached(string location, bool attached)
    {
        var archive = Find(location) ?? throw new KeyNotFoundException($"{location} is not a listed archive.");
        _archives = _archives.Select(item => item == archive ? item with { Attached = attached } : item).ToList();
        Save();
    }

    /// <summary>Detaches or re-attaches one archived folder. Nothing on disk is touched.</summary>
    public void SetFolderAttached(string location, string label, bool attached)
    {
        var archive = Find(location) ?? throw new KeyNotFoundException($"{location} is not a listed archive.");
        var detached = (archive.DetachedFolders ?? []).Where(item => !string.Equals(item, label, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!attached) detached.Add(label);
        var updated = archive with { DetachedFolders = detached.Count == 0 ? null : detached.Order(StringComparer.OrdinalIgnoreCase).ToArray() };
        _archives = _archives.Select(item => item == archive ? updated : item).ToList();
        Save();
    }

    /// <summary>Takes a location off the list. Nothing on disk is touched.</summary>
    public void Remove(string location)
    {
        var archive = Find(location);
        if (archive is null) return;
        _archives = _archives.Where(item => item != archive).ToList();
        Save();
    }

    /// <summary>The archived folders in a location, labelled by where they sit inside it.</summary>
    public IReadOnlyList<Folder> Folders(Archive archive)
    {
        if (!IsAvailable(archive)) return [];
        var folders = new List<Folder>();
        foreach (var file in Directory.EnumerateFiles(archive.Location, FolderBundle.FileName, SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = System.IO.Path.GetRelativePath(archive.Location, System.IO.Path.GetDirectoryName(file)!);
            var label = relative == "." ? System.IO.Path.GetFileName(archive.Location) : relative.Replace('\\', '/');
            if (archive.IsFolderDetached(label))
            {
                // Detached: listed so it can be attached again, but its file is not opened.
                folders.Add(new(archive, label, file, 0, 0, Attached: false));
                continue;
            }
            try
            {
                using var bundle = FolderBundle.Open(file);
                var messages = bundle.List();
                folders.Add(new(archive, label, file, messages.Count, messages.Count(message => !message.IsRead)));
            }
            catch (Exception error) when (error is InvalidDataException or SqliteException or IOException)
            {
                // Not a readable folder file; it is left alone and simply not offered.
            }
        }
        return folders;
    }

    public IReadOnlyList<MessageSummary> List(Folder folder)
    {
        var slot = Slot(folder.File);
        using var bundle = FolderBundle.Open(folder.File);
        return bundle.List().Select(message => Summary(slot, folder.Archive.Name, folder.Label, message)).ToArray();
    }

    /// <summary>Searches every attached archive that is present. Archives that are detached or not found are
    /// named in the result, so a search never silently claims to have covered them.</summary>
    public SearchResult Search(string query, int limit = 500)
        => Search(query, limit, null);

    public SearchResult Search(string query, int limit, string? sender)
    {
        SearchCatalog.ValidateSenderFilter(sender);
        var match = FtsQuery.Parse(query).Match;
        var hits = new List<MessageSummary>();
        var notSearched = new List<string>();
        foreach (var archive in _archives)
        {
            if (!archive.Attached) { notSearched.Add($"{archive.Name} (detached)"); continue; }
            if (!IsAvailable(archive)) { notSearched.Add($"{archive.Name} (not found)"); continue; }
            foreach (var folder in Folders(archive))
            {
                if (!folder.Attached) { notSearched.Add($"{archive.Name} / {folder.Label} (detached)"); continue; }
                using var bundle = FolderBundle.Open(folder.File);
                IReadOnlyList<FolderBundle.Summary> found;
                try { found = bundle.Search(match, limit, sender); }
                catch (SqliteException) { found = bundle.Search(FallbackMatch(query), limit, sender); }
                var slot = Slot(folder.File);
                hits.AddRange(found.Select(message => Summary(slot, archive.Name, folder.Label, message)));
            }
        }
        return new(hits.OrderByDescending(hit => hit.Date).Take(limit).ToArray(), notSearched);
    }

    public MessageContent? Get(long id)
    {
        var (file, seq, uuid) = Locate(id);
        var (archive, label) = Describe(file);
        using var bundle = FolderBundle.Open(file);
        if (bundle.Folder().Uuid != uuid) throw new KeyNotFoundException("That archive folder has been replaced; select the message again.");
        var message = bundle.Get(seq);
        if (message is null) return null;
        return MailDatabase.ContentFromRaw(Summary(Slot(file), archive, label, message.Summary), Cache(message));
    }

    public string ExtractAttachment(long id, int index)
    {
        var content = Get(id) ?? throw new KeyNotFoundException("The message is no longer in its archive.");
        return MailDatabase.ExtractAttachmentFromRaw(content.RawPath!, index,
            System.IO.Path.Combine(_paths.PreviewDirectory, "attachments", "archive", (-id).ToString()));
    }

    private Archive? Find(string location)
    {
        var full = System.IO.Path.GetFullPath(location);
        return _archives.FirstOrDefault(item => string.Equals(item.Location, full, StringComparison.OrdinalIgnoreCase));
    }

    private (string Archive, string Label) Describe(string file)
    {
        var archive = _archives.FirstOrDefault(item => file.StartsWith(item.Location + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var relative = archive is null ? System.IO.Path.GetDirectoryName(file)! : System.IO.Path.GetRelativePath(archive.Location, System.IO.Path.GetDirectoryName(file)!);
        return (archive?.Name ?? "Archive", relative.Replace('\\', '/'));
    }

    private int Slot(string file)
    {
        using var bundle = FolderBundle.Open(file);
        var uuid = bundle.Folder().Uuid;
        var index = _slots.FindIndex(slot => string.Equals(slot.File, file, StringComparison.OrdinalIgnoreCase) && slot.Uuid == uuid);
        if (index >= 0) return index + 1;
        _slots.Add((file, uuid));
        return _slots.Count;
    }

    private (string File, long Seq, string Uuid) Locate(long id)
    {
        if (!Owns(id)) throw new ArgumentOutOfRangeException(nameof(id), "Not an archived message id.");
        var slot = (int)(-id / Stride);
        if (slot < 1 || slot > _slots.Count) throw new KeyNotFoundException("That archive is no longer open.");
        var folder = _slots[slot - 1];
        return (folder.File, -id % Stride, folder.Uuid);
    }

    private static string DefaultName(string location)
        => System.IO.Path.GetFileName(location.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : location;

    /// <summary>Plain terms, each quoted, for input FTS5 refuses after translation.</summary>
    private static string FallbackMatch(string query) => string.Join(" AND ",
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(term => '"' + term.Replace("\"", "\"\"") + '"'));

    private List<Archive> Load()
    {
        try
        {
            return File.Exists(RegistryPath) ? JsonSerializer.Deserialize<List<Archive>>(File.ReadAllText(RegistryPath)) ?? [] : [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Written beside, then moved over, so a crash mid-write never loses the list.</summary>
    private void Save()
    {
        Directory.CreateDirectory(_paths.Root);
        var temporary = RegistryPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_archives, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, RegistryPath, overwrite: true);
    }

    /// <summary>Content-addressed, like the main blob store, so the same message is written once.</summary>
    private string Cache(FolderBundle.Message message)
    {
        var directory = System.IO.Path.Combine(_paths.PreviewDirectory, "archive-cache", message.Summary.Sha256[..2]);
        var file = System.IO.Path.Combine(directory, message.Summary.Sha256 + ".eml");
        if (File.Exists(file)) return file;
        Directory.CreateDirectory(directory);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, message.Raw);
        try { File.Move(temporary, file); }
        catch (IOException) when (File.Exists(file)) { File.Delete(temporary); }
        return file;
    }

    /// <summary>Account shows the archive's name and Folder its folder, as a stored message shows its own.</summary>
    private static MessageSummary Summary(int slot, string archive, string label, FolderBundle.Summary message) => new(
        -(slot * Stride + message.Seq), Source, archive, label, message.Seq.ToString(), null, message.Subject, message.Sender,
        message.SenderName, message.Recipients.Split(", ", StringSplitOptions.RemoveEmptyEntries),
        DateTimeOffset.TryParse(message.DateUtc, out var date) ? date : null, message.Size, message.IsRead,
        message.AttachmentCount, message.Snippet, MessageRef: $"archive:{slot}/{message.Seq}");
}
