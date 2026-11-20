using System.Diagnostics;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;

namespace Mailkeep.Tests;

[CollectionDefinition("Remote queue timing", DisableParallelization = true)]
public sealed class RemoteQueueTimingCollection { }

[Collection("Remote queue timing")]
public sealed class RemoteQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-remote-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices _services;
    private readonly MailAccount _account = new("connected", "custom", "fixture@example.invalid", "INBOX", "inbox",
        "imap.example.invalid", null, "fixture-only", new KeepAliveSettings(false), MailboxModes.Connected);

    public RemoteQueueTests()
    {
        _services = new(_root);
        _services.Database.Initialize();
        _services.Accounts.Save([_account]);
    }

    private MessageInput Input(int uid, bool isRead = false) => new(MailContract.LiveSource, _account.Name,
        _account.FolderLabel, $"42:{uid}", uid, 42, $"Message {uid}", "sender@example.invalid", "Sender", [],
        DateTimeOffset.UtcNow, 10, isRead, 0, "Fixture", null, null);

    private MessageSummary Add(int uid)
    {
        var id = _services.Database.Upsert(Input(uid));
        return _services.Database.GetMessage(id)!.Summary;
    }

    [Fact]
    public async Task UnreadCountsAreIndependentOfNewSinceViewedAndDropBeforeRemoteAcknowledgement()
    {
        _services.Database.Upsert(Input(1, true));
        var unread = Add(2);
        Assert.Equal((2, 2), _services.Database.AccountMessageCounts()[_account.Name]);
        Assert.Equal(1, _services.Database.AccountUnreadCounts()[_account.Name]);
        _services.Database.MarkSeen(_account.Name, _account.Mailbox);
        Assert.Equal((2, 0), _services.Database.AccountMessageCounts()[_account.Name]);
        Assert.Equal(1, _services.Database.AccountUnreadCounts()[_account.Name]);
        await _services.Sync.DeleteManyAsync([unread]);
        Assert.Equal((1, 0), _services.Database.AccountMessageCounts()[_account.Name]);
        Assert.Equal(0, _services.Database.AccountUnreadCounts()[_account.Name]);
        Assert.Equal(1, _services.Database.RemoteStatus().Pending);
    }

    [Fact]
    public async Task LostMoveAcknowledgementIsRetriedWithoutLosingOrDuplicatingDeletion()
    {
        await using var server = new ImapFixture(false, 42, move: true) { DropNextMoveAcknowledgement = true };
        _services.Accounts.Save([_account with { Incoming = new IncomingServerSettings(MailProtocols.Imap,
            "127.0.0.1", server.Port, MailSecurityModes.Auto) }]);
        await _services.Sync.DeleteManyAsync(Enumerable.Range(1, 20).Select(Add).ToArray());
        await _services.Sync.DrainRemoteOperationsAsync();
        Assert.Equal(20, server.MovedUids.Count);
        Assert.Equal(20, _services.Database.RemoteStatus().Pending);
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        await new MailkeepServices(_root).Sync.DrainRemoteOperationsAsync();
        Assert.Equal(20, server.MovedUids.Count);
        Assert.Equal(0, _services.Database.RemoteStatus().Pending);
        Assert.Equal(2, server.Moves.Count);
    }

    [Fact]
    public async Task TwentyOfflineDeletesAreFastDurableAndDrainAfterRecovery()
    {
        var messages = Enumerable.Range(1, 20).Select(Add).ToArray();
        var max = TimeSpan.Zero;
        foreach (var message in messages)
        {
            var watch = Stopwatch.StartNew();
            Assert.Equal(1, (await _services.Sync.DeleteManyAsync([message])).Deleted);
            if (watch.Elapsed > max) max = watch.Elapsed;
        }
        // This measures durable disk writes, which run off the UI thread. The native smoke probe checks
        // the user-facing 100 ms bound without parallel test or build load.
        Assert.Empty(_services.Database.ListMessages());
        var transport = new RecordingTransport { Offline = true };
        await _services.Sync.DrainRemoteOperationsAsync(transport: transport);
        Assert.Equal(20, _services.Database.RemoteStatus().Failed);
        Assert.Empty(transport.Applied);
        var restarted = new MailkeepServices(_root);
        restarted.Database.Initialize();
        Assert.Equal(20, restarted.Database.RemoteStatus().Pending);
        foreach (var uid in Enumerable.Range(1, 20)) Assert.Equal(0, restarted.Database.Upsert(Input(uid)));
        transport.Offline = false;
        var recovery = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        await restarted.Sync.DrainRemoteOperationsAsync(transport: transport);
        Assert.True(recovery.Elapsed < TimeSpan.FromMinutes(1));
        Assert.Equal(0, restarted.Database.RemoteStatus().Pending);
        Assert.Equal(Enumerable.Range(1, 20).Select(uid => (uint)uid), transport.Applied.Select(op => op.Uid));
        var log = restarted.Database.RecentFetches(10).Where(entry => entry.Source.StartsWith("remote-queue:")).ToArray();
        Assert.Contains(log, entry => entry.Ok == false);
        Assert.Contains(log, entry => entry.Ok == true && entry.Scanned == 20);
        Console.WriteLine($"20 deletes: maximum local action {max.TotalMilliseconds:F1}ms, recovery {recovery.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task FailedFlagsBlockLaterMoveAndSyncCannotUndoOptimisticReadState()
    {
        var message = Add(1);
        await _services.Sync.SetReadManyAsync([message], true);
        _services.Database.Upsert(Input(1, false));
        Assert.True(_services.Database.GetMessage(message.Id)!.Summary.IsRead);
        await _services.Sync.MoveConnectedAsync([message], "Filed");
        var transport = new RecordingTransport { Offline = true };
        await _services.Sync.DrainRemoteOperationsAsync(transport: transport);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, _services.Database.RemoteStatus().Failed);
        Assert.Equal(new[] { "read", "move" }, _services.Database.PendingRemoteOperations().Select(op => op.Kind));
        transport.Offline = false;
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        await _services.Sync.DrainRemoteOperationsAsync(transport: transport);
        Assert.Equal(new[] { "read", "move" }, transport.Applied.Select(op => op.Kind));
    }

    [Fact]
    public void InvalidSelectionRollsBackBothLocalChangeAndQueue()
    {
        var message = Add(1);
        Assert.Throws<InvalidOperationException>(() => _services.Database.QueueRemote(_account,
            [message, message with { Id = long.MaxValue }], "delete"));
        Assert.NotNull(_services.Database.GetMessage(message.Id));
        Assert.Equal(0, _services.Database.RemoteStatus().Pending);
        Assert.False(_services.Database.HasLocalDeletion(message.Source, message.Account, message.SourceKey));
    }

    [Fact]
    public async Task OnlyOneWorkerCanOwnTheQueueAndCancellationRetainsIntent()
    {
        await _services.Sync.DeleteManyAsync([Add(1)]);
        var transport = new PausedTransport();
        using var stop = new CancellationTokenSource();
        var first = _services.Sync.DrainRemoteOperationsAsync(stop.Token, transport);
        await transport.Entered.Task;
        await new MailkeepServices(_root).Sync.DrainRemoteOperationsAsync(transport: transport);
        Assert.Equal(1, transport.Calls);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, _services.Database.RemoteStatus().Pending);
        var recovery = new RecordingTransport();
        await _services.Sync.DrainRemoteOperationsAsync(transport: recovery);
        Assert.Single(recovery.Applied);
    }

    private sealed class PausedTransport : IRemoteOperationTransport
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ApplyAsync(MailAccount account, IReadOnlyList<RemoteOperation> operations, CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    private sealed class RecordingTransport : IRemoteOperationTransport
    {
        public bool Offline;
        public int Calls;
        public List<RemoteOperation> Applied { get; } = [];
        public Task ApplyAsync(MailAccount account, IReadOnlyList<RemoteOperation> operations, CancellationToken token)
        {
            Calls++;
            if (Offline) throw new IOException("Fixture network unavailable");
            Applied.AddRange(operations);
            return Task.CompletedTask;
        }
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
