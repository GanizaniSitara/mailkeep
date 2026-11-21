using System.Diagnostics;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Tests;

public sealed class OperationalCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-catalog-test-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices services;
    public OperationalCatalogTests()
    {
        services = new(root);
        services.Database.Initialize();
        Sql(services.Paths.DatabasePath, "INSERT INTO messages(source,account,folder,source_key,subject,body_text) VALUES('live','fixture','Inbox','a','Alpha','first'),('live','fixture','Inbox','b','Beta','second')");
    }
    private static object? Sql(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
        using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar();
    }

    [Fact]
    public async Task UnchangedCatalogSearchDoesNotWaitForAnUncommittedWriter()
    {
        services.Catalog.SyncOperational();
        using var connection = new SqliteConnection($"Data Source={services.Paths.SearchCatalogPath};Pooling=False");
        connection.Open();
        using var held = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = held;
        command.CommandText = "UPDATE source_state SET count=count+1 WHERE source='operational'";
        command.ExecuteNonQuery();
        var reader = Task.Run(() =>
        {
            var other = new MailkeepServices(root);
            Assert.Equal(0, other.Catalog.SyncOperational());
            return other.Catalog.Search("Alpha");
        });
        bool completed;
        try { completed = await Task.WhenAny(reader, Task.Delay(2000)) == reader; }
        finally { held.Rollback(); }
        var result = await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(completed, "An unchanged catalog search waited for the writer instead of reading committed results.");
        Assert.Single(result.Results);
    }

    [Fact]
    public void OneChangedMessageDoesNotRewriteUnchangedCatalogRows()
    {
        Assert.Equal(2, services.Catalog.SyncOperational());
        var unchanged = Sql(services.Paths.SearchCatalogPath, "SELECT id FROM documents WHERE local_id=2");
        Sql(services.Paths.DatabasePath, "UPDATE messages SET subject='Changed',body_text='orchid',is_read=1 WHERE id=1");
        Assert.Equal(1, services.Catalog.SyncOperational());
        Assert.Equal(unchanged, Sql(services.Paths.SearchCatalogPath, "SELECT id FROM documents WHERE local_id=2"));
        Assert.Single(services.Catalog.Search("orchid").Results);
        Assert.Equal(0, services.Catalog.SyncOperational());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InitializationStillRemovesLegacyCatalogDocumentsAndState(bool documents, bool state)
    {
        services.Catalog.SyncOperational();
        if (documents)
            Sql(services.Paths.SearchCatalogPath, "UPDATE documents SET source='oe_archive' WHERE local_id=1");
        if (state)
            Sql(services.Paths.SearchCatalogPath, "INSERT INTO source_state VALUES('oe_archive','old',1,'2026-01-01')");
        new MailkeepServices(root).Catalog.Initialize();
        Assert.Equal(0L, Sql(services.Paths.SearchCatalogPath, "SELECT count(*) FROM documents WHERE source='oe_archive'"));
        Assert.Equal(0L, Sql(services.Paths.SearchCatalogPath, "SELECT count(*) FROM source_state WHERE source='oe_archive'"));
        Assert.Equal(documents ? 1L : 2L, Sql(services.Paths.SearchCatalogPath, "SELECT count(*) FROM documents_fts"));
        Assert.Single(services.Catalog.Search("Beta").Results);
    }

    [Fact]
    public void InsertDeleteAndSourceTransitionAreIncremental()
    {
        services.Catalog.SyncOperational();
        Sql(services.Paths.DatabasePath, "INSERT INTO messages(source,account,folder,source_key,subject) VALUES('live','fixture','Inbox','c','Gamma')");
        Assert.Equal(1, services.Catalog.SyncOperational());
        Sql(services.Paths.DatabasePath, "DELETE FROM messages WHERE id=1");
        Assert.Equal(1, services.Catalog.SyncOperational());
        Assert.Empty(services.Catalog.Search("Alpha").Results);
        Sql(services.Paths.DatabasePath, "UPDATE messages SET source='archive' WHERE id=2");
        Assert.Equal(1, services.Catalog.SyncOperational());
        Assert.Empty(services.Catalog.Search("Beta").Results);
        Assert.Single(services.Catalog.Search("Gamma").Results);
    }

    [Fact]
    public async Task WaitingForTransportDoesNotHoldTheMailboxWriteLock()
    {
        services.Identities.Save([new SendingIdentity("fixture", null, "Fixture", "sender@example.invalid", null, true, "custom",
            new OutgoingServerSettings("smtp.example.invalid", 465), null, null, "fixture-password", SendingIdentityStates.Ready)]);
        var transport = new WaitingTransport();
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, transport);
        var draft = sender.SaveDraft("fixture", ["recipient@example.invalid"], "Fixture", "Fixture body");
        await sender.QueueAsync(draft.Id);
        var sending = sender.SendQueuedAsync([draft.Id]);
        try
        {
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False;Default Timeout=1"); c.Open();
            using var q = c.CreateCommand(); q.CommandTimeout = 1;
            q.CommandText = "INSERT INTO settings VALUES('transport-write-probe','ok')";
            Assert.Equal(1, q.ExecuteNonQuery());
        }
        finally { transport.Release.TrySetResult(); }
        Assert.True(Assert.Single(await sending).Ok);
    }

    private sealed class WaitingTransport : IOutgoingTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default)
        { Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken); }
    }

    [Fact]
    public async Task ConcurrentConsumersAndCancelledWaitDoNotLoseChanges()
    {
        services.Catalog.SyncOperational();
        Sql(services.Paths.DatabasePath, "UPDATE messages SET subject='Moved',folder='Destination' WHERE id=1");
        using (var held = new FileStream(services.Paths.SearchCatalogPath + ".operational.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => services.Catalog.SyncOperational(cancellationToken: cancel.Token)));
        var results = await Task.WhenAll(Task.Run(() => services.Catalog.SyncOperational()),
            Task.Run(() => new MailkeepServices(root).Catalog.SyncOperational()));
        Assert.Equal(new[] { 0, 1 }, results.Order());
        Assert.Single(services.Catalog.Search("Moved", folder: "Destination").Results);
        Sql(services.Paths.DatabasePath, "UPDATE messages SET body_text='later revision' WHERE id=1");
        Assert.Equal(1, services.Catalog.SyncOperational());
        Assert.Single(services.Catalog.Search("later").Results);
    }

    [Fact]
    public void AccountModeChangesAndMissingCatalogRebuildWithoutLosingMail()
    {
        services.Catalog.SyncOperational();
        Assert.Equal(2, services.Catalog.Search("Alpha OR Beta", "connected").Results.Count);
        services.Accounts.Save([new MailAccount("fixture", "custom", "fixture@example.invalid", "INBOX", "Inbox",
            "imap.example.invalid", null, null, new KeepAliveSettings(), MailboxModes.LocalKeep)]);
        Assert.Equal(2, services.Catalog.SyncOperational());
        Assert.Empty(services.Catalog.Search("Alpha OR Beta", "connected").Results);
        Assert.Equal(2, services.Catalog.Search("Alpha OR Beta", "local").Results.Count);
        File.Delete(services.Paths.SearchCatalogPath);
        Assert.Equal(2, services.Catalog.SyncOperational());
        Assert.Single(services.Catalog.Search("Alpha").Results);
        Assert.Equal(0, services.Catalog.SyncOperational());
    }

    [Fact]
    public async Task CliOutboxAndEmptySendSurviveABusySearchCatalog()
    {
        var executable = Environment.GetEnvironmentVariable("MAILKEEP_TEST_CLI");
        if (executable is null) return;
        services.Catalog.SyncOperational();
        // An up-to-date catalog needs no write, even while another process holds the writer.
        Sql(services.Paths.DatabasePath, "UPDATE messages SET subject='Pending index change' WHERE id=1");
        Assert.Equal(0L, Sql(services.Paths.DatabasePath, "SELECT count(*) FROM outgoing_messages"));
        using var connection = new SqliteConnection($"Data Source={services.Paths.SearchCatalogPath};Pooling=False");
        connection.Open();
        using var held = connection.BeginTransaction();
        foreach (var command in new[] { "outbox", "send" })
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(command);
            if (command == "send") start.ArgumentList.Add("--execute"); // Empty disposable Outbox; no identity or network transport exists.
            start.ArgumentList.Add("--root"); start.ArgumentList.Add(root);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            using var result = JsonDocument.Parse(await stdout);
            if (command == "send")
            {
                Assert.Equal(0, result.RootElement.GetProperty("sent").GetInt32());
                Assert.Contains("pending", result.RootElement.GetProperty("catalog_warning").GetString());
            }
            else Assert.Equal(0, result.RootElement.GetArrayLength());
        }
    }

    // Opt-in measurement on a disposable operational-only copy of an immutable backup.
    // Never calls SMTP, opens the GUI, or writes to the source mailbox/backup.
    [Fact]
    public void MeasureOperationalCatalogOnBackupCopy()
    {
        var sourcePath = Environment.GetEnvironmentVariable("MAILKEEP_CATALOG_BENCH_SOURCE");
        var output = Environment.GetEnvironmentVariable("MAILKEEP_CATALOG_BENCH_OUTPUT");
        if (sourcePath is null || output is null) return;
        using (var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False"))
        using (var target = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
        {
            source.Open(); target.Open();
            Sql(services.Paths.DatabasePath, "DELETE FROM messages");
            using var query = source.CreateCommand();
            query.CommandText = "SELECT id,source,account,folder,source_key,subject,sender,sender_name,recipients_json,date_utc,size,is_read,attachment_count,body_text,raw_path FROM messages WHERE source='live'";
            using var rows = query.ExecuteReader();
            using var tx = target.BeginTransaction();
            using var insert = target.CreateCommand(); insert.Transaction = tx;
            var names = Enumerable.Range(0, rows.FieldCount).Select(rows.GetName).ToArray();
            insert.CommandText = $"INSERT INTO messages({string.Join(',', names)}) VALUES({string.Join(',', names.Select(n => "$" + n))})";
            foreach (var name in names) insert.Parameters.AddWithValue("$" + name, DBNull.Value);
            insert.Prepare();
            while (rows.Read())
            {
                for (var i = 0; i < names.Length; i++) insert.Parameters[i].Value = rows.GetValue(i);
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var clock = Stopwatch.StartNew();
        var indexed = services.Catalog.SyncOperational(force: true);
        var first = clock.Elapsed.TotalSeconds;
        clock.Restart();
        services.Catalog.SyncOperational(force: true);
        var forced = clock.Elapsed.TotalSeconds;
        Sql(services.Paths.DatabasePath, "UPDATE messages SET is_read=1-is_read WHERE id=(SELECT min(id) FROM messages)");
        clock.Restart();
        var changed = services.Catalog.SyncOperational();
        var incremental = clock.Elapsed.TotalSeconds;
        clock.Restart();
        var idle = services.Catalog.SyncOperational();
        File.WriteAllText(output, JsonSerializer.Serialize(new { indexed, firstSeconds = first,
            forcedSeconds = forced, changed, changedSeconds = incremental, idle, idleSeconds = clock.Elapsed.TotalSeconds }));
    }

    public void Dispose()
    {
        if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(root).StartsWith("mailkeep-catalog-test-")) Directory.Delete(root, true);
    }
}
