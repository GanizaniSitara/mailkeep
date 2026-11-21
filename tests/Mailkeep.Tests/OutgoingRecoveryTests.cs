using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Tests;

public sealed class OutgoingRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-send-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly MailkeepServices services;
    private readonly Transport transport = new();
    private readonly MailSendService send;

    public OutgoingRecoveryTests()
    {
        services = new(root); services.Database.Initialize();
        services.Identities.Save([new("fixture", null, "Fixture", "fixture@example.invalid", null, true, "custom",
            new("127.0.0.1", 1, MailSecurityModes.None), null, null, "synthetic", SendingIdentityStates.Ready)]);
        send = new(services.Identities, services.Database, services.Blobs, transport);
    }

    private async Task<string> Queued()
    {
        var draft = send.SaveDraft("fixture", ["recipient@example.invalid"], "Synthetic recovery", "Invented content");
        await send.QueueAsync(draft.Id);
        return draft.Id;
    }

    private void Age(string id)
    {
        using var connection = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outgoing_messages SET updated_at=$time WHERE id=$id";
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.AddHours(-1).UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }

    [Fact]
    public async Task StaleRowsAreSurfacedButNeverAutomaticallyRedelivered()
    {
        var id = await Queued();
        Assert.NotNull(services.Database.ClaimOutgoing(id)); Age(id);
        var reopened = new MailkeepServices(root);
        Assert.Equal(id, Assert.Single(reopened.Database.ListStaleSending()).Id);
        Assert.Contains("may or may not", Assert.Single(reopened.Database.ListOutbox()).LastError);
        Assert.Empty(await send.SendPendingAsync());
        Assert.False(Assert.Single(await send.SendQueuedAsync([id])).Ok);
        Assert.Equal(0, transport.Calls);
        services.Database.RecoverStaleSending(id, markAsSent: true);
        Assert.True((await send.CompleteAcceptedAsync(id)).Ok);
        Assert.Equal(OutgoingStates.Sent, services.Database.GetOutgoing(id)!.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task OnlyExplicitReturnToOutboxAllowsAnotherSend()
    {
        var id = await Queued(); services.Database.ClaimOutgoing(id);
        Assert.Throws<InvalidOperationException>(() => services.Database.RecoverStaleSending(id, false));
        Age(id);
        Assert.Equal(OutgoingStates.Outbox, services.Database.RecoverStaleSending(id, false).Status);
        Assert.Equal(0, transport.Calls);
        Assert.True(Assert.Single(await send.SendQueuedAsync([id])).Ok);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task RecoveryCannotRequeueAnActiveSenderEvenWhenItsTimestampIsOld()
    {
        var id = await Queued(); transport.Hold = true;
        var sending = send.SendQueuedAsync([id]);
        try
        {
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Age(id);
            Assert.Throws<IOException>(() => services.Database.RecoverStaleSending(id, false));
        }
        finally { transport.Release.TrySetResult(); await sending; }
        Assert.Equal(1, transport.Calls);
        Assert.Equal(OutgoingStates.Sent, services.Database.GetOutgoing(id)!.Status);
    }

    private sealed class Transport : IOutgoingTransport
    {
        public int Calls;
        public bool Hold;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default)
        {
            Calls++; Started.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(cancellationToken);
        }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
