using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Returns archived folders to Local Mail. Mailbox commit precedes archive removal.
/// Same-directory recovery snapshots and a durable intent make interrupted file moves resumable.</summary>
public sealed class ArchiveMoveBack(MailkeepServices services)
{
    public const string PendingFile = ".mailkeep-restore-pending.json";
    private sealed record Pending(int Version, string Id, string Database, ArchiveRestore.Plan Plan,
        int Restored, int AlreadyRestored, string[] FolderKeys);

    public static ArchiveRestore.Plan? PendingPlan(string location) => ReadPending(location)?.Plan;

    public static void EnsureNoPending(string location)
    {
        if (File.Exists(Path.Combine(location, PendingFile)))
            throw new InvalidOperationException("Finish the interrupted Restore to Local Mail before copying or archiving more folders here.");
    }

    internal static IDisposable Lock(string location)
        => new FileStream(Path.Combine(Path.GetFullPath(location), ".mailkeep-archive-operation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    public ArchiveRestore.Result Run(string location, string selection = "", bool includeChildren = false,
        string into = "", bool execute = false, CancellationToken token = default)
    {
        location = Path.GetFullPath(location);
        using var lease = execute ? Lock(location) : null;
        var pending = ReadPending(location);
        if (pending is not null)
        {
            if (!Path.GetFullPath(pending.Database).Equals(services.Paths.DatabasePath, StringComparison.OrdinalIgnoreCase)
                || !pending.Plan.Selection.Equals(selection, StringComparison.OrdinalIgnoreCase)
                || pending.Plan.IncludeChildren != includeChildren || !pending.Plan.Into.Equals(into, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Another Restore needs to finish first. Use Finish Interrupted Restore on this archive.");
            if (!execute) return new(false, pending.Plan, 0, pending.Plan.Messages, pending.FolderKeys);
        }
        else
        {
            var result = new ArchiveRestore(services).Run(location, selection, includeChildren, into, execute, token);
            if (!execute) return result;
            pending = new(1, Guid.NewGuid().ToString("N"), services.Paths.DatabasePath,
                result.Plan, result.Restored, result.AlreadyRestored, result.FolderKeys);
            var intent = Path.Combine(location, PendingFile);
            var temporary = intent + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(pending));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, intent); // If this fails, both copies remain; an ordinary retry is idempotent.
        }
        try
        {
            Finish(pending, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new IOException("The messages are committed in Local Mail, but the archive transfer needs to finish. "
                + "Use Finish Interrupted Restore on the archive. No recovery files have been deleted.\n\n" + error.Message, error);
        }
        return new(true, pending.Plan, pending.Restored, pending.AlreadyRestored, pending.FolderKeys);
    }

    private void Finish(Pending pending, CancellationToken token)
    {
        var location = pending.Plan.Location;
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = services.Paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        target.Open();
        // Hold an immediate transaction while checking retained occurrences. Concurrent local writes cannot commit
        // between verification and finishing the file moves.
        using var transaction = target.BeginTransaction();
        var sourceLocks = new List<FileStream>();
        try
        {
            foreach (var entry in pending.Plan.Folders)
            {
                token.ThrowIfCancellationRequested();
                var source = ArchiveManifest.ResolveFile(location, entry.File);
                var recovery = source + ".restored-" + pending.Id + ".bak";
                if (File.Exists(source) && File.Exists(recovery)) throw new InvalidDataException("Both archive and recovery files exist; neither will be overwritten.");
                var file = File.Exists(source) ? source : recovery;
                // Exclude source writers through verification and rename, while allowing our same-volume rename.
                sourceLocks.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
                if (File.Exists(file + "-wal") && new FileInfo(file + "-wal").Length > 0)
                    throw new IOException("An archive folder has uncheckpointed changes; close other users of that archive and retry.");
                if (ArchiveManifest.TruthSignature(file) != entry.Signature) throw new InvalidDataException("Archive changed after the Local Mail copy was committed.");
                using var archive = FolderBundle.Open(file);
                if (archive.Folder().Uuid != entry.Uuid) throw new InvalidDataException("Archive identity changed.");
                using var command = target.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT r.seq,r.sha256,m.raw_path FROM archive_restore_messages r "
                    + "JOIN messages m ON m.id=r.message_id WHERE r.folder_uuid=$uuid ORDER BY r.seq";
                command.Parameters.AddWithValue("$uuid", entry.Uuid);
                var count = 0;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var raw = archive.Raw(reader.GetInt64(0)) ?? throw new InvalidDataException("An archived occurrence is missing.");
                    var hash = reader.GetString(1);
                    if (FolderBundle.Sha256(raw) != hash || reader.IsDBNull(2)) throw new InvalidDataException("A retained message differs from its archive.");
                    using var blob = services.Blobs.OpenRead(reader.GetString(2));
                    if (Convert.ToHexStringLower(SHA256.HashData(blob)) != hash) throw new InvalidDataException("A Local Mail copy failed its independent byte check.");
                    count++;
                }
                if (count != entry.Messages) throw new InvalidDataException("Not every archive occurrence is still retained in Local Mail.");
            }
            // Verification above covers the whole selection before the first file leaves the Archive view.
            foreach (var entry in pending.Plan.Folders)
            {
                token.ThrowIfCancellationRequested();
                var source = ArchiveManifest.ResolveFile(location, entry.File);
                if (File.Exists(source)) File.Move(source, source + ".restored-" + pending.Id + ".bak");
            }
            var inventory = ArchiveManifest.Read(location);
            var removed = pending.Plan.Folders.Select(entry => entry.Uuid).ToHashSet(StringComparer.Ordinal);
            ArchiveManifest.Save(location, new(1, inventory.Folders.Where(entry => !removed.Contains(entry.Uuid)).ToArray()));
            File.Delete(Path.Combine(location, PendingFile));
            transaction.Commit();
        }
        finally { foreach (var stream in sourceLocks) stream.Dispose(); }
    }

    private static Pending? ReadPending(string location)
    {
        location = Path.GetFullPath(location);
        var file = Path.Combine(location, PendingFile);
        if (!File.Exists(file)) return null;
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(file)) ?? throw new InvalidDataException("Empty restore intent.");
        if (pending.Version != 1 || !Guid.TryParseExact(pending.Id, "N", out _)
            || !Path.GetFullPath(pending.Plan.Location).Equals(location, StringComparison.OrdinalIgnoreCase)
            || pending.Plan.Folders.Select(entry => entry.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pending.Plan.Folders.Length)
            throw new InvalidDataException("Invalid restore intent.");
        foreach (var entry in pending.Plan.Folders)
        {
            ArchiveManifest.ResolveFile(location, entry.File);
            ArchiveManifest.ValidatePath(entry.OriginalPath);
        }
        return pending;
    }
}
