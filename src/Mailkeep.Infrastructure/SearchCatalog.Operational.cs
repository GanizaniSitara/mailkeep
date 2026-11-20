using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

public sealed partial class SearchCatalog
{
    public int SyncOperational(bool force = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _paths.EnsureDirectories();
        // Serialize consumers before reading their checkpoint, not after preparing an older
        // snapshot. The source mailbox stays writable while the catalog is updated.
        using var lease = OperationalLease(cancellationToken);
        InitializeCore(5);
        if (!File.Exists(_paths.DatabasePath)) return 0;
        using var catalog = Open(5);
        string? previous;
        var previousCount = 0;
        using (var state = catalog.CreateCommand())
        {
            state.CommandText = "SELECT generation,count FROM source_state WHERE source='operational'";
            using var row = state.ExecuteReader();
            previous = row.Read() ? row.GetString(0) : null;
            if (previous is not null) previousCount = row.GetInt32(1);
        }
        var modes = _accounts.Load().ToDictionary(a => a.Name, a => a.IsConnected, StringComparer.OrdinalIgnoreCase);
        var modeHash = Hash(JsonSerializer.Serialize(modes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new { Account = p.Key.ToLowerInvariant(), Connected = p.Value })));
        var changed = new List<long>();
        var archiveChanged = new List<long>();
        var documents = new List<CatalogDocument>();
        var archiveDocuments = new List<CatalogDocument>();
        string generation;
        bool rebuild;
        using (var source = new SqliteConnection($"Data Source={_paths.DatabasePath};Mode=ReadOnly;Pooling=False;Default Timeout=5"))
        {
            source.Open();
            using var cancellation = InterruptOnCancellation(source, cancellationToken);
            using var snapshot = source.BeginTransaction(deferred: true);
            using var query = source.CreateCommand(); query.Transaction = snapshot;
            query.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='search_operational_changes' AND type='table'";
            var tracked = Convert.ToInt64(query.ExecuteScalar()) == 1;
            query.CommandText = "SELECT value FROM settings WHERE key='search.operational.epoch'";
            var epoch = query.ExecuteScalar() as string;
            query.CommandText = "SELECT value FROM settings WHERE key='search.operational.change-sequence'";
            var revision = Convert.ToInt64(query.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
            var prefix = $"operational-v2:{epoch}:{modeHash}:";
            long last = -1;
            rebuild = force || !tracked || epoch is null || previous is null
                || !previous.StartsWith(prefix, StringComparison.Ordinal)
                || !long.TryParse(previous[prefix.Length..], out last) || last > revision;
            generation = prefix + revision.ToString(CultureInfo.InvariantCulture);
            if (!rebuild && last == revision) return 0;
            if (!rebuild)
            {
                query.CommandText = "SELECT message_id FROM search_operational_changes WHERE revision>$last AND revision<=$revision ORDER BY revision,message_id";
                query.Parameters.AddWithValue("$last", last);
                query.Parameters.AddWithValue("$revision", revision);
                using var ids = query.ExecuteReader();
                while (ids.Read()) { cancellationToken.ThrowIfCancellationRequested(); changed.Add(ids.GetInt64(0)); }
            }
            foreach (var batch in rebuild ? new[] { "" } : Batches(changed.ToArray()))
            {
                query.Parameters.Clear();
                query.CommandText = """
                    SELECT id,account,folder,source_key,subject,sender,sender_name,recipients_json,date_utc,
                           size,is_read,attachment_count,body_text,raw_path
                    FROM messages WHERE source='live'
                    """ + (rebuild ? "" : $" AND id IN ({batch})");
                using var rows = query.ExecuteReader();
                while (rows.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var account = rows.GetString(1);
                    documents.Add(new CatalogDocument(
                        account == MailContract.LocalCopyAccount || !modes.GetValueOrDefault(account, true)
                            ? MailContract.LocalSource : MailContract.ConnectedSource,
                        account, rows.GetString(2), rows.GetString(3), rows.GetInt64(0), rows.GetString(4),
                        rows.GetString(5), rows.GetString(6), JsonSerializer.Deserialize<string[]>(rows.GetString(7)) ?? [],
                        ParseDate(rows, 8), rows.GetInt64(9), rows.GetInt32(10) != 0, rows.GetInt32(11), rows.GetString(12),
                        RawPath: rows.IsDBNull(13) ? null : rows.GetString(13)));
                }
            }
            // Imported folder moves use the same durable change sequence. Refresh
            // only those locations, even when archive counts/read flags did not change.
            if (BulkFolderMigration.HasTable(source, "search_archive_changes"))
            {
                query.Parameters.Clear();
                query.CommandText = "SELECT message_id FROM search_archive_changes WHERE revision>$last AND revision<=$revision ORDER BY revision,message_id";
                query.Parameters.AddWithValue("$last", rebuild ? -1 : last);
                query.Parameters.AddWithValue("$revision", revision);
                using var ids = query.ExecuteReader();
                while (ids.Read()) { cancellationToken.ThrowIfCancellationRequested(); archiveChanged.Add(ids.GetInt64(0)); }
            }
            archiveDocuments.AddRange(ReadArchive(source, archiveChanged, cancellationToken));
            snapshot.Commit();
        }
        // Both the source revision and changed rows came from one read snapshot. Later
        // writes retain higher revisions; no acknowledgement deletes their change records.
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = catalog.BeginTransaction();
        var removed = 0;
        foreach (var batch in rebuild ? new[] { "" } : Batches(changed.ToArray()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var where = "source IN ('connected','local')" + (rebuild ? "" : $" AND local_id IN ({batch})");
            using var delete = catalog.CreateCommand(); delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM documents_fts WHERE rowid IN (SELECT id FROM documents WHERE {where})";
            delete.ExecuteNonQuery();
            delete.CommandText = "DELETE FROM documents WHERE " + where;
            removed += delete.ExecuteNonQuery();
        }
        // An imported occurrence can now move to Local Mail without changing its ID.
        // Remove its old imported search location in the same incremental transaction.
        var movedImports = documents.Where(d => d.LocalId is not null
            && d.SourceKey == MailContract.AdoptedCopyKeyPrefix + d.LocalId).Select(d => d.LocalId!.Value)
            .Concat(archiveDocuments.Select(d => d.LocalId!.Value)).Distinct().ToArray();
        foreach (var batch in Batches(movedImports))
        {
            using var delete = catalog.CreateCommand(); delete.Transaction = transaction;
            var where = $"source='archive' AND local_id IN ({batch})";
            delete.CommandText = $"DELETE FROM documents_fts WHERE rowid IN (SELECT id FROM documents WHERE {where}); DELETE FROM documents WHERE {where}";
            delete.ExecuteNonQuery();
        }
        foreach (var document in documents.Concat(archiveDocuments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Insert(catalog, transaction, document);
        }
        cancellationToken.ThrowIfCancellationRequested();
        SetState(catalog, transaction, "operational", generation,
            rebuild ? documents.Count : previousCount - removed + documents.Count);
        transaction.Commit();
        return rebuild ? documents.Count : changed.Concat(archiveChanged).Distinct().Count();
    }

    private FileStream OperationalLease(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(_paths.SearchCatalogPath + ".operational.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && watch.Elapsed < TimeSpan.FromSeconds(5))
            { Thread.Sleep(25); }
        }
    }
}
