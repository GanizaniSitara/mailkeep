using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Portable inventory of the archive tree. A missing child file is an error, not an empty folder.</summary>
public static class ArchiveManifest
{
    public const string FileName = "archive-tree.json";
    public const string LayoutEvent = "original_folder_layout";
    public sealed record Layout(string Path, string Kind, string Origin, string? OriginKey);
    public sealed record Entry(string File, string Label, string OriginalPath, string Uuid, int Messages, string Signature);
    public sealed record Inventory(int Version, Entry[] Folders);

    public static string ValidatePath(string path)
    {
        var parts = path.Split('/');
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Contains('\\') || part.Any(char.IsControl)))
            throw new InvalidDataException($"Invalid archive folder path: {path}");
        return path;
    }

    public static string ResolveFile(string location, string relative)
    {
        ValidatePath(relative);
        var root = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) != FolderBundle.FileName)
            throw new InvalidDataException("An archive file lies outside its location.");
        return file;
    }

    public static Inventory Read(string location)
    {
        var path = Path.Combine(location, FileName);
        var result = File.Exists(path)
            ? JsonSerializer.Deserialize<Inventory>(System.IO.File.ReadAllText(path)) ?? throw new InvalidDataException("Empty archive inventory.")
            : Capture(location);
        if (result.Version != 1 || result.Folders.Select(entry => entry.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Folders.Length
            || result.Folders.Select(entry => entry.Uuid).Distinct(StringComparer.Ordinal).Count() != result.Folders.Length
            || result.Folders.Select(entry => entry.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Folders.Length
            || result.Folders.Select(entry => entry.OriginalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Folders.Length)
            throw new InvalidDataException("Unsupported or ambiguous archive inventory.");
        foreach (var entry in result.Folders)
        {
            ResolveFile(location, entry.File);
            ValidatePath(entry.Label);
            ValidatePath(entry.OriginalPath);
        }
        return result;
    }

    public static Inventory Capture(string location) => new(1, Directory.EnumerateFiles(location, FolderBundle.FileName, SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase).Select(file => Describe(location, file)).ToArray());

    public static Entry Describe(string location, string file)
    {
        using var bundle = FolderBundle.Open(file);
        var meta = bundle.Folder();
        var label = Path.GetRelativePath(location, Path.GetDirectoryName(file)!).Replace('\\', '/');
        if (label == ".") label = meta.Name;
        var layout = bundle.LastEventDetail(LayoutEvent) is { } json ? JsonSerializer.Deserialize<Layout>(json) : null;
        var original = layout?.Path ?? string.Join('/', label.Split('/').SkipLast(1).Append(meta.Name));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages";
        return new(Path.GetRelativePath(location, file).Replace('\\', '/'), ValidatePath(label), ValidatePath(original), meta.Uuid,
            Convert.ToInt32(command.ExecuteScalar()), TruthSignature(file));
    }

    /// <summary>Fingerprints portable truth only. Header/FTS cache loss or rebuild does not change it.</summary>
    public static string TruthSignature(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sql in new[] { "SELECT * FROM folder", "SELECT m.seq,m.sha256,m.size,m.received_at,s.is_read,s.flagged,s.deleted_at,s.origin,s.added_at,s.copied_from,s.legacy_id FROM messages m LEFT JOIN state s ON s.seq=m.seq ORDER BY m.seq" })
        {
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()) + "\n"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static Inventory Register(string location, string file)
    {
        var inventory = Read(location);
        var entry = Describe(location, file);
        var updated = new Inventory(1, inventory.Folders.Where(old => !old.File.Equals(entry.File, StringComparison.OrdinalIgnoreCase)).Append(entry)
            .OrderBy(folder => folder.File, StringComparer.OrdinalIgnoreCase).ToArray());
        Save(location, updated);
        return updated;
    }

    public static void Save(string location, Inventory inventory)
    {
        var file = Path.Combine(location, FileName);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(temporary, file, overwrite: true);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
}
