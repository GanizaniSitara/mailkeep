using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Tests;

public sealed class OeDraftConversionTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "mailkeep-convert-" + Guid.NewGuid().ToString("N")));
    private readonly MailkeepServices _services;
    public OeDraftConversionTests()
    {
        _services = new MailkeepServices(_paths.Root);
        _services.Database.Initialize();
        _services.Identities.Save([new SendingIdentity("fixture", null, "Fixture", "sender@example.invalid", null, true, "custom",
            new OutgoingServerSettings("smtp.example.invalid", 465), null, null, null, SendingIdentityStates.NeedsAuthorization)]);
    }
    [Fact]
    public async Task RetirementKeepsVisibleDraftAndReceiptWithoutAnotherBackup()
    {
        var retired = await Add("retire", "Drafts");
        var active = await Add("keep", "Outbox");
        var conversion = new OeDraftConversion(_paths);
        var converted = conversion.Apply(new OeDraftConversionReport(_paths).Run());
        Assert.True(_services.Send.DiscardDraft("oe_draft_" + retired));
        var visible = _services.Database.GetOutgoing("oe_draft_" + active)!;
        var reviewed = Review(retired);
        var backupBytes = File.ReadAllBytes(converted.Backup);
        var result = conversion.RetireOriginals([reviewed], converted.Backup);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.ReceiptsRetained);
        Assert.Null(_services.Database.GetMessage(retired));
        Assert.NotNull(_services.Database.GetMessage(active));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(visible),
            System.Text.Json.JsonSerializer.Serialize(_services.Database.GetOutgoing("oe_draft_" + active)));
        Assert.Single(Directory.GetFiles(_paths.DataDirectory, "*.bak"));
        Assert.Equal(backupBytes, File.ReadAllBytes(converted.Backup));
        Assert.Equal(0, await Add("retire", "Drafts"));
        Assert.Empty(new OeDraftConversionReport(_paths).Run().Candidates);
        Assert.Throws<InvalidDataException>(() => conversion.RetireOriginals([reviewed], converted.Backup));
    }

    [Fact]
    public async Task RetirementRefusesWholeBatchWhenOneDraftIsActiveOrReceiptChanged()
    {
        var retired = await Add("retire", "Drafts");
        var active = await Add("keep", "Outbox");
        var conversion = new OeDraftConversion(_paths);
        var converted = conversion.Apply(new OeDraftConversionReport(_paths).Run());
        Assert.True(_services.Send.DiscardDraft("oe_draft_" + retired));
        var reviewed = Review(retired);
        Assert.Throws<InvalidDataException>(() => conversion.RetireOriginals([reviewed, Review(active)], converted.Backup));
        Assert.NotNull(_services.Database.GetMessage(retired));
        Assert.NotNull(_services.Database.GetMessage(active));
        Assert.Throws<InvalidDataException>(() => conversion.RetireOriginals([reviewed with { Sha256 = new string('0', 64) }], converted.Backup));
        Assert.Throws<InvalidDataException>(() => conversion.RetireOriginals([reviewed, reviewed], converted.Backup));
        Assert.NotNull(_services.Database.GetMessage(retired));
        Assert.Single(_services.Database.ListOutgoing());
    }

    private OeDraftConversion.RetirementOriginal Review(long id)
    {
        using var c = new SqliteConnection($"Data Source={_paths.DatabasePath};Pooling=False");
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT m.source,m.account,m.folder,m.source_key,m.raw_path,r.draft_id,r.sha256 FROM messages m JOIN oe_draft_conversions r ON r.message_id=m.id WHERE m.id=$id";
        q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader(); Assert.True(r.Read());
        return new(id, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));
    }

    [Fact]
    public async Task ConversionKeepsOriginalsAndIndependentCopiesWithoutQueueingAndBlocksReimport()
    {
        var first = await Add("a", "Outbox");
        var second = await Add("b", "Drafts");
        var approved = new OeDraftConversionReport(_paths).Run();
        var result = new OeDraftConversion(_paths).Apply(approved);
        Assert.Equal(2, result.Converted);
        Assert.True(File.Exists(result.Backup));
        using (var backup = new SqliteConnection("Data Source=" + result.Backup + ";Mode=ReadOnly;Pooling=False"))
        {
            backup.Open(); using var query = backup.CreateCommand(); query.CommandText = "SELECT count(*) FROM outgoing_messages";
            Assert.Equal(0L, query.ExecuteScalar());
        }
        Assert.Empty(_services.Database.ListMessages(source: "archive"));
        Assert.NotNull(_services.Database.GetMessage(first));
        Assert.NotNull(_services.Database.GetMessage(second));
        Assert.Null(_services.Database.ClaimNextOutgoing());
        Assert.Empty(_services.Database.ListOutgoing(OutgoingStates.Outbox));
        var drafts = _services.Database.ListOutgoing(OutgoingStates.Draft);
        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, draft =>
        {
            Assert.Equal("fixture", draft.SendingIdentityId);
            Assert.Equal(["cc@example.invalid"], draft.Cc);
            Assert.Equal(["bcc@example.invalid"], draft.Bcc);
            Assert.Contains("data:image/png", draft.BodyHtml);
            Assert.Equal(approved.Candidates.Single(row => "oe_draft_" + row.Id == draft.Id).RawPath, draft.RawPath);
            Assert.Equal(0, draft.AttemptCount);
            Assert.Contains(_services.Database.GetOutgoingAttachments(draft.Id), part => part.FileName == "retained.txt");
        });
        Assert.Empty(new OeDraftConversionReport(_paths).Run().Candidates);
        Assert.Equal(0, await Add("a", "Outbox"));
        Assert.Equal(0, await Add("b", "Drafts"));
        Assert.Equal(2, _services.Database.ListOutgoing(OutgoingStates.Draft).Count);
        _services.Send.SaveDraft("fixture", [], "Edited", "Edited body", id: drafts[0].Id);
        Assert.Equal("Edited", _services.Database.GetOutgoing(drafts[0].Id)!.Subject);
        Assert.True(_services.Send.DiscardDraft(drafts[0].Id));
        Assert.Equal(0, await Add("a", "Outbox"));
        Assert.Empty(new OeDraftConversionReport(_paths).Run().Candidates);
        _services.Catalog.Initialize();
        _services.Catalog.SyncArchive();
        Assert.Empty(_services.Catalog.Search("Conversionfixture").Results);
    }
    [Fact]
    public async Task ChangedReportAndMissingIdentityLeaveMailUnchanged()
    {
        await Add("a", "Outbox");
        var report = new OeDraftConversionReport(_paths).Run();
        await Add("b", "Drafts");
        Assert.Throws<InvalidDataException>(() => new OeDraftConversion(_paths).Apply(report));
        Assert.Empty(_services.Database.ListOutgoing());
        _services.Identities.Save([]);
        Assert.Throws<InvalidDataException>(() => new OeDraftConversion(_paths).Apply(new OeDraftConversionReport(_paths).Run()));
        Assert.Equal(2, _services.Database.ListMessages(source: "archive").Count);
        Assert.Empty(_services.Database.ListOutgoing());
    }
    [Fact]
    public async Task LaterDraftCollisionRollsBackEntireConversion()
    {
        await Add("a", "Outbox");
        var last = await Add("b", "Drafts");
        _services.Database.SaveDraft("fixture", [], [], [], "Existing", "Body", id: "oe_draft_" + last);
        Assert.Throws<SqliteException>(() => new OeDraftConversion(_paths).Apply(new OeDraftConversionReport(_paths).Run()));
        Assert.Equal(2, _services.Database.ListMessages(source: "archive").Count);
        Assert.Equal("Existing", Assert.Single(_services.Database.ListOutgoing()).Subject);
        Assert.Equal(2, new OeDraftConversionReport(_paths).Run().Candidates.Length);
    }
    private async Task<long> Add(string key, string folder)
    {
        var builder = new BodyBuilder { TextBody = "Conversionfixture body", HtmlBody = "<p>Conversionfixture body</p><img src='cid:pixel'>" };
        builder.Attachments.Add("retained.txt", System.Text.Encoding.UTF8.GetBytes("retained bytes"));
        var inline = builder.LinkedResources.Add("pixel.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        inline.ContentId = "pixel";
        using var message = new MimeMessage { Subject = "Conversionfixture", Body = builder.ToMessageBody(), MessageId = "fixed@example.invalid", Date = DateTimeOffset.Parse("2026-10-06T12:00:00Z") };
        message.From.Add(MailboxAddress.Parse("sender@example.invalid"));
        message.To.Add(MailboxAddress.Parse("to@example.invalid"));
        message.Cc.Add(MailboxAddress.Parse("cc@example.invalid"));
        message.Bcc.Add(MailboxAddress.Parse("bcc@example.invalid"));
        using var raw = new MemoryStream(); message.WriteTo(raw); raw.Position = 0;
        return await _services.Database.UpsertMimeMessageAsync("archive", "OE Classic", folder, key, null, 0, message, raw, false);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_paths.Root, true); }
}
