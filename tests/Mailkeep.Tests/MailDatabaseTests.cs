using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class MailDatabaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleArchiveCountsExcludeOwnedCopiesAcrossFoldersAndDeletedAdoptions(bool missingDate)
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var input = SampleMessage("owned", null, "Owned", "Body") with
        {
            Account = "local", Folder = LocalFolderKinds.Deleted, IsRead = true,
            Date = missingDate ? null : DateTimeOffset.Parse("2026-08-01T10:00:00Z")
        };
        services.Database.Upsert(input);
        services.Database.Upsert(input with { Source = MailContract.ArchiveSource, Account = "archive", Folder = "Inbox", IsRead = false });
        services.Database.Upsert(input with { Source = MailContract.ArchiveSource, Account = "archive", Folder = "Inbox", SourceKey = "unread", Subject = "Unread", IsRead = false });
        services.Database.Upsert(input with { Source = MailContract.ArchiveSource, Account = "archive", Folder = "Inbox", SourceKey = "read", Subject = "Read" });
        services.Database.Upsert(input with { Account = "connected", Subject = "Connected", SourceKey = "connected" });
        services.Database.Upsert(input with { Source = MailContract.ArchiveSource, Account = "archive", Folder = "Inbox", Subject = "Connected", SourceKey = "connected", IsRead = false });
        var adopted = services.Database.Upsert(input with { Source = MailContract.ArchiveSource, Account = "archive", Folder = "Inbox", SourceKey = "adopted", Subject = "Adopted", IsRead = false });
        var copy = services.Database.Upsert(input with { Account = MailContract.LocalCopyAccount, Folder = LocalFolderKinds.Deleted,
            SourceKey = MailContract.AdoptedCopyKeyPrefix + adopted, Subject = "Adopted" });
        services.Database.DeleteMessageLocally(copy);

        Assert.Equal((3, 2), services.Database.VisibleArchiveFolderCounts(["local", MailContract.LocalCopyAccount])[("archive", "Inbox")]);
        Assert.Equal((4, 3), services.Database.VisibleArchiveFolderCounts([])[("archive", "Inbox")]);
        // The fast unread-only count the folder tree uses must agree with the full pass.
        Assert.Equal(2, services.Database.ArchiveUnreadCounts(["LOCAL", MailContract.LocalCopyAccount])[("archive", "Inbox")]);
        Assert.Equal(3, services.Database.ArchiveUnreadCounts([])[("archive", "Inbox")]);
    }

    [Fact]
    public async Task ReplyAndForwardMarksAppearOnlyAfterDelivery()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity()]);
        var original = services.Database.Upsert(SampleMessage("original", null, "Question", "Body") with { Account = "local" });
        var other = services.Database.Upsert(SampleMessage("other", null, "Other", "Body") with { Account = "local" });
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, new FakeOutgoingTransport());
        var failing = new MailSendService(services.Identities, services.Database, services.Blobs, new FakeOutgoingTransport(new IOException("offline")));

        // A reply kept as a draft, and a forward abandoned, mark nothing.
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Re: Question", "Reply");
        services.Database.SetOutgoingOrigin(draft.Id, original, MessageMarkKinds.Replied);
        var discarded = sender.SaveDraft("sender", ["recipient@example.invalid"], "Fw: Other", "Forward");
        services.Database.SetOutgoingOrigin(discarded.Id, other, MessageMarkKinds.Forwarded);
        Assert.True(services.Database.DeleteDraft(discarded.Id));
        Assert.Empty(services.Database.MessageMarks([original, other]));

        // A send that fails marks nothing either; the message waits in the Outbox.
        await sender.QueueAsync(draft.Id);
        Assert.False(Assert.Single(await failing.SendQueuedAsync([draft.Id])).Ok);
        Assert.Empty(services.Database.MessageMarks([original, other]));

        // Delivered: replied, and it survives a fresh store object as it would a restart.
        Assert.True(Assert.Single(await sender.SendQueuedAsync([draft.Id])).Ok);
        var forward = sender.SaveDraft("sender", ["recipient@example.invalid"], "Fw: Question", "Forward");
        services.Database.SetOutgoingOrigin(forward.Id, original, MessageMarkKinds.Forwarded);
        await sender.QueueAsync(forward.Id);
        Assert.True(Assert.Single(await sender.SendQueuedAsync([forward.Id])).Ok);
        var reopened = new MailkeepServices(temp.Path).Database.MessageMarks([original, other]);
        Assert.Equal(new MessageMarks(true, true), Assert.Single(reopened).Value);
        Assert.Equal(original, reopened.Keys.Single());
    }

    [Fact]
    public async Task FilingAnAnsweredImportedMessagePreservesItsReplyAndForwardMarks()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity()]);
        var original = services.Database.Upsert(SampleMessage("original", null, "Question", "Body") with
        { Source = MailContract.ArchiveSource, Account = "archive", Folder = "Inbox" });
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, new FakeOutgoingTransport());
        foreach (var kind in new[] { MessageMarkKinds.Replied, MessageMarkKinds.Forwarded })
        {
            var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Answer", "Body");
            services.Database.SetOutgoingOrigin(draft.Id, original, kind);
            await sender.QueueAsync(draft.Id);
            Assert.True(Assert.Single(await sender.SendQueuedAsync([draft.Id])).Ok);
        }
        services.Database.AdoptMessages([original], LocalFolderKinds.Inbox);
        var copy = Assert.Single(services.Database.ListMessages().Where(message => message.IsLocalStoredCopy));
        Assert.Equal(new MessageMarks(true, true), services.Database.MessageMarks([copy.Id]).GetValueOrDefault(copy.Id));
        services.Database.MoveMessage(copy.Id, LocalFolderKinds.Deleted);
        Assert.Equal(new MessageMarks(true, true), new MailkeepServices(temp.Path).Database.MessageMarks([copy.Id]).GetValueOrDefault(copy.Id));
    }

    [Fact]
    public void PicturesAreCopiedOutOnceEachNamedByDayAndScopedToTheFolderAsked()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var photo = Enumerable.Range(0, 20_000).Select(index => (byte)(index % 251)).ToArray();
        var other = Enumerable.Range(0, 20_000).Select(index => (byte)(index % 241)).ToArray();
        var tiny = new byte[500];
        long Add(string source, string folder, string key, DateTimeOffset date, Action<MimeKit.BodyBuilder> build)
        {
            var builder = new MimeKit.BodyBuilder { TextBody = "Body" };
            build(builder);
            using var message = new MimeKit.MimeMessage { Body = builder.ToMessageBody() };
            using var bytes = new MemoryStream();
            message.WriteTo(bytes);
            var raw = services.Blobs.Put(bytes.ToArray());
            return services.Database.Upsert(SampleMessage(key, null, key, "Body") with
            {
                Source = source, Account = source == MailContract.ArchiveSource ? "archive" : "local", Folder = folder,
                Date = date, AttachmentCount = builder.Attachments.Count, RawPath = raw
            });
        }
        var day = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
        Add(MailContract.LiveSource, LocalFolderKinds.Inbox, "first", day, builder =>
        {
            builder.Attachments.Add("holiday.jpg", photo, new MimeKit.ContentType("image", "jpeg"));
            builder.Attachments.Add("notes.txt", "text"u8.ToArray(), new MimeKit.ContentType("text", "plain"));
            builder.Attachments.Add("icon.png", tiny, new MimeKit.ContentType("image", "png"));
            builder.HtmlBody = "<p>Body</p><img src=\"cid:logo@x\">";
            builder.LinkedResources.Add("logo.png", other, new MimeKit.ContentType("image", "png")).ContentId = "logo@x";
        });
        // The same picture sent again: copied once.
        Add(MailContract.LiveSource, LocalFolderKinds.Inbox, "again", day.AddDays(1), builder =>
            builder.Attachments.Add("holiday.jpg", photo, new MimeKit.ContentType("image", "jpeg")));
        Add(MailContract.ArchiveSource, "Trips/2019", "archived", day.AddYears(-7), builder =>
            builder.Attachments.Add("beach.png", other, new MimeKit.ContentType("image", "png")));

        var inbox = Path.Combine(temp.Path, "inbox-pictures");
        var result = services.Database.ExportPictures(new PictureExportRequest(inbox, LocalFolders: [LocalFolderKinds.Inbox]));
        Assert.Equal((2, 1, 1, 1), (result.Messages, result.Written, result.Duplicates, result.Small));
        var file = Assert.Single(Directory.GetFiles(inbox));
        Assert.Equal(day.LocalDateTime.ToString("yyyy-MM-dd") + " holiday.jpg", Path.GetFileName(file));
        Assert.Equal(photo, File.ReadAllBytes(file));
        Assert.Equal(day.LocalDateTime, File.GetLastWriteTime(file));
        // Run again: nothing new is written.
        Assert.Equal(0, services.Database.ExportPictures(new PictureExportRequest(inbox, LocalFolders: [LocalFolderKinds.Inbox])).Written);
        // Asked for, the picture written into the body comes too.
        Assert.Equal(1, services.Database.ExportPictures(new PictureExportRequest(inbox, LocalFolders: [LocalFolderKinds.Inbox], IncludeInline: true)).Written);
        // An imported folder and what is beneath it; and all mail, which also holds it.
        var trips = Path.Combine(temp.Path, "trips");
        Assert.Equal(1, services.Database.ExportPictures(new PictureExportRequest(trips, ArchiveFolder: "Trips")).Written);
        Assert.Equal(0, services.Database.ExportPictures(new PictureExportRequest(Path.Combine(temp.Path, "none"), ArchiveFolder: "Trips", IncludeSubfolders: false)).Written);
        Assert.Equal(2, services.Database.ExportPictures(new PictureExportRequest(Path.Combine(temp.Path, "all"))).Written);
    }

    [Fact]
    public void ContactsAreOnePerAddressAndImportOnlyAddsWhatIsNew()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var ann = services.Database.SaveContact(new Contact(0, " Ann Example ", "ann@example.invalid"));
        Assert.Equal("Ann Example", ann.Name);
        Assert.Throws<InvalidOperationException>(() => services.Database.SaveContact(new Contact(0, "Duplicate", "ANN@example.invalid")));
        Assert.Throws<ArgumentException>(() => services.Database.SaveContact(new Contact(0, "Broken", "not an address")));
        services.Database.SaveContact(ann with { Name = "Smith, Ann", Notes = "edited" });

        var imported = new[]
        {
            new Contact(0, "Ann From OE", "ann@example.invalid", Origin: OeAddressBook.Origin, OriginKey: "1"),
            new Contact(0, "Bob", "bob@example.invalid", Origin: OeAddressBook.Origin, OriginKey: "2"),
            new Contact(0, "Nobody", "", Origin: OeAddressBook.Origin, OriginKey: "3")
        };
        Assert.Equal((1, 1, 1), services.Database.ImportContacts(imported));
        Assert.Equal((0, 2, 1), services.Database.ImportContacts(imported));
        var contacts = services.Database.ListContacts();
        Assert.Equal(["Bob", "Smith, Ann"], contacts.Select(contact => contact.Name));
        Assert.Equal("edited", contacts.Single(contact => contact.Email == "ann@example.invalid").Notes);
        Assert.True(services.Database.DeleteContact(contacts[0].Id));
        Assert.Single(services.Database.ListContacts());
    }

    [Fact]
    public void FilingImportedMailAdoptsItOnceAndTheImportStandsDown()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var archived = services.Database.Upsert(SampleMessage("todo", null, "Todo", "Body") with
        {
            Source = MailContract.ArchiveSource, Account = "archive", Folder = "_Pinned/2026-10",
            Date = DateTimeOffset.Parse("2026-10-01T10:00:00Z")
        });

        Assert.Equal(1, services.Database.AdoptMessages([archived], LocalFolderKinds.Inbox));
        var inbox = services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], LocalFolderKinds.Inbox, 10);
        Assert.Equal("Todo", Assert.Single(inbox).Subject);
        // The import's copy no longer counts as unread anywhere: the mailbox holds its own.
        Assert.Equal(0, services.Database.ArchiveUnreadCounts([MailContract.LocalCopyAccount]).GetValueOrDefault(("archive", "_Pinned/2026-10")));
        // Filed again from the import (a stale row): the one copy moves, no second one appears.
        Assert.Equal(1, services.Database.AdoptMessages([archived], LocalFolderKinds.Deleted));
        Assert.Empty(services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], LocalFolderKinds.Inbox, 10));
        Assert.Single(services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], LocalFolderKinds.Deleted, 10));
    }

    [Fact]
    public void TakingBackAnImportedFilingRemovesTheCopyAndTheImportShowsAgain()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var archived = services.Database.Upsert(SampleMessage("todo", null, "Todo", "Body") with
        {
            Source = MailContract.ArchiveSource, Account = "archive", Folder = "_Pinned/2026-10", IsRead = false,
            Date = DateTimeOffset.Parse("2026-10-01T10:00:00Z")
        });
        Assert.Empty(services.Database.AdoptedCopies([archived]));
        services.Database.AdoptMessages([archived], LocalFolderKinds.Inbox);
        var copy = Assert.Single(services.Database.AdoptedCopies([archived])).Value;
        Assert.Equal(LocalFolderKinds.Inbox, copy.Folder);
        Assert.Equal(0, services.Database.ArchiveUnreadCounts([MailContract.LocalCopyAccount]).GetValueOrDefault(("archive", "_Pinned/2026-10")));

        Assert.True(services.Database.RemoveMessage(copy.Id));
        Assert.Empty(services.Database.AdoptedCopies([archived]));
        Assert.Equal(1, services.Database.ArchiveUnreadCounts([MailContract.LocalCopyAccount])[("archive", "_Pinned/2026-10")]);
        // Filed again afterwards, it is adopted afresh: nothing left behind refuses it.
        Assert.Equal(1, services.Database.AdoptMessages([archived], LocalFolderKinds.Inbox));
    }

    [Fact]
    public void TakingBackADeleteReturnsTheMessageAndForgetsTheDeletion()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var input = SampleMessage("pop:1", null, "Kept", "Body") with { Account = "local", Folder = "user:todo" };
        var id = services.Database.Upsert(input);
        Assert.True(services.Database.DeleteMessageLocally(id));
        Assert.Equal(LocalFolderKinds.Deleted, services.Database.MessageFolders([id])[id]);

        Assert.True(services.Database.RestoreDeleted(id, "user:todo"));
        Assert.Equal("user:todo", services.Database.MessageFolders([id])[id]);
        // Already back: a second undo finds nothing in Deleted Items to return.
        Assert.False(services.Database.RestoreDeleted(id, "user:todo"));
        // The deletion is forgotten: removed and fetched again, the message is taken in, not refused.
        Assert.True(services.Database.RemoveMessage(id));
        Assert.NotEqual(0, services.Database.Upsert(input));
    }

    [Fact]
    public void AFolderListsEveryMessageItHoldsNotJustTheNewest()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        // More than any cap the list has had (1,000 in the app, 5,000 in the query). Written in one statement:
        // one Upsert apiece takes minutes.
        const int held = 5_100;
        using (var connection = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < $held)
                INSERT INTO messages (source, account, folder, source_key, subject, date_utc)
                SELECT $source, 'archive', 'zz_Deleted', 'bulk-' || i, 'Bulk ' || i,
                       strftime('%Y-%m-%dT%H:%M:%SZ', '2010-01-01', '+' || i || ' hours')
                FROM n
                """;
            command.Parameters.AddWithValue("$held", held);
            command.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
            Assert.Equal(held, command.ExecuteNonQuery());
        }

        var listed = services.Database.ListMessagesForAccounts(["archive"], "zz_Deleted", int.MaxValue, MailContract.ArchiveSource);
        Assert.Equal(held, listed.Count);
        Assert.Equal("Bulk 0", listed[^1].Subject);
        Assert.Equal(held, services.Database.ListMessages("archive", MailContract.ArchiveSource, int.MaxValue).Count);
    }

    [Fact]
    public async Task AddressHistoryComesFromSentMailAndRemovedAddressesReturnOnlyWhenWrittenToAgain()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity()]);
        services.Database.Upsert(SampleMessage("s1", null, "One", "Body") with
        {
            Source = MailContract.ArchiveSource, Account = "OE Classic", Folder = "Archive_2023/Sent",
            Recipients = ["Ann@example.invalid", "bob@example.invalid"], Date = DateTimeOffset.Parse("2026-01-01T10:00:00Z")
        });
        services.Database.Upsert(SampleMessage("s2", null, "Two", "Body") with
        {
            Source = MailContract.ArchiveSource, Account = "OE Classic", Folder = "Sent Items",
            Recipients = ["ann@example.invalid"], Date = DateTimeOffset.Parse("2026-02-01T10:00:00Z")
        });
        // Not sent mail, and a former employer's archive: neither is offered.
        services.Database.Upsert(SampleMessage("in", null, "In", "Body") with
        {
            Source = MailContract.ArchiveSource, Account = "OE Classic", Folder = "Inbox", Recipients = ["inbox@example.invalid"]
        });
        services.Database.Upsert(SampleMessage("work", null, "Work", "Body") with
        {
            Source = MailContract.ArchiveSource, Account = "Online Archive - me@work.invalid", Folder = "Sent Items",
            Recipients = ["colleague@example.invalid"]
        });

        var history = services.Database.ListAddressHistory();
        Assert.Equal(["ann@example.invalid", "bob@example.invalid"], history.Select(use => use.Email));
        Assert.Equal(2, history[0].Uses);

        Assert.True(services.Database.RemoveFromAddressHistory("BOB@example.invalid"));
        Assert.Equal(["ann@example.invalid"], new MailkeepServices(temp.Path).Database.ListAddressHistory().Select(use => use.Email));

        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, new FakeOutgoingTransport());
        var draft = sender.SaveDraft("sender", ["Bob Example <bob@example.invalid>", "new@example.invalid"], "Hi", "Body");
        await sender.QueueAsync(draft.Id);
        Assert.True(Assert.Single(await sender.SendQueuedAsync([draft.Id])).Ok);
        history = services.Database.ListAddressHistory();
        Assert.Equal(["bob@example.invalid", "new@example.invalid"], history.Take(2).Select(use => use.Email).Order());
        Assert.Equal(2, history.Single(use => use.Email == "bob@example.invalid").Uses);
    }

    [Fact]
    public void OeAddressBookIsReadFromACopyAndNeverChanged()
    {
        using var temp = new TempDirectory();
        var book = Path.Combine(temp.Path, "_AddressBook.db");
        using (var connection = new SqliteConnection($"Data Source={book};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE [addressbook] ([id] INTEGER PRIMARY KEY NOT NULL,[type] INTEGER NOT NULL,[identity] INTEGER NOT NULL,[folder] INTEGER NOT NULL,[gender] INTEGER NOT NULL,[photo] BLOB,[name_first] TEXT,[name_middle] TEXT,[name_last] TEXT,[name_nick] TEXT,[name_display] TEXT,[email_personal] TEXT,[email_work] TEXT,[email_other] TEXT,[email_primary] INTEGER,[email_format] INTEGER NOT NULL,[notes] TEXT);
                INSERT INTO addressbook VALUES(1,0,1,0,0,NULL,'Ann',NULL,'Example',NULL,'','ann@example.invalid','',NULL,0,0,'note');
                INSERT INTO addressbook VALUES(2,0,1,0,0,NULL,NULL,NULL,NULL,'Bobby','','','bob.work@example.invalid','bob@example.invalid',2,0,NULL);
                INSERT INTO addressbook VALUES(3,0,1,0,0,NULL,'No',NULL,'Address',NULL,'','','','',0,0,NULL);
                """;
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(book);

        var contacts = OeAddressBook.Read(book);

        Assert.Equal(before, File.ReadAllBytes(book));
        Assert.Equal(["_AddressBook.db"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
        Assert.Equal([("Ann Example", "ann@example.invalid", "note"), ("Bobby", "bob@example.invalid", "")],
            contacts.Select(contact => (contact.Name, contact.Email, contact.Notes)));
        Assert.All(contacts, contact => Assert.Equal(OeAddressBook.Origin, contact.Origin));
    }

    [Fact]
    public void UpsertIsIdempotentAndSearchReturnsQualifiedMessageColumns()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var input = SampleMessage("first", 7, "Quarterly invoice", "The searchable body");

        var first = services.Database.Upsert(input);
        var second = services.Database.Upsert(input with { Subject = "Updated quarterly invoice" });
        var result = services.Database.Search("quarterly searchable", MailContract.LiveSource, 10);

        Assert.Equal(first, second);
        Assert.Equal(1, result.Count);
        Assert.Single(result.Results);
        Assert.Equal("Updated quarterly invoice", result.Results[0].Subject);
        Assert.Equal("sender@example.invalid", result.Results[0].Sender);
    }


    [Fact]
    public void UnifiedCatalogSearchesAllSourcesAndCollapsesDuplicateLocations()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([
            new MailAccount("primary", "gmail", "one@example.invalid", "INBOX", "Connected", null, null, null, new KeepAliveSettings()),
            new MailAccount("local", "custom", "two@example.invalid", "INBOX", "Local", null, null, "secret", new KeepAliveSettings(),
                MailboxModes.LocalKeep, new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995))
        ]);
        var duplicate = SampleMessage("connected-copy", 1, "Quarterly invoice", "Shared searchable body");
        services.Database.Upsert(duplicate);
        services.Database.Upsert(duplicate with { Account = "local", Folder = "Local", SourceKey = "local-copy", ServerUid = null });
        services.Catalog.SyncOperational(force: true);
        services.Catalog.ReplaceArchive([
            new CatalogDocument(MailContract.ArchiveSource, "OE Classic", "Archive", "archive-copy", null,
                duplicate.Subject, duplicate.Sender, duplicate.SenderName, duplicate.Recipients, duplicate.Date,
                duplicate.Size, false, 0, duplicate.BodyText),
            new CatalogDocument(MailContract.ArchiveSource, "OE Classic", "Archive", "archive-only", null,
                "Quarterly archive", "other@example.invalid", "Other", [], duplicate.Date?.AddDays(-1), 999,
                false, 0, "Another searchable body")
        ], "fixture-1");

        var result = services.Catalog.Search("quarterly", "all", 20);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.Results.Count);
        var shared = result.Results.Single(item => item.Subject == "Quarterly invoice");
        Assert.Equal(MailContract.LocalSource, shared.Source);
        Assert.Equal(3, shared.Locations?.Count);
        Assert.StartsWith("msg_", shared.MessageRef);
        Assert.NotNull(services.Catalog.Resolve(shared.MessageRef!));
    }

    [Fact]
    public void ArchivedMailIsIndexedFromThisStoreAndReindexedOnlyWhenItChanges()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Database.Upsert(SampleMessage("archived", null, "Historical note", "Searchable archive body")
            with { Source = MailContract.ArchiveSource, Account = "OE Classic", Folder = "Archive" });

        var first = services.Catalog.SyncArchive();
        var second = services.Catalog.SyncArchive();
        var result = services.Catalog.Search("historical searchable", MailContract.ArchiveSource, 10);

        Assert.Equal(1, first);
        // Nothing changed, so nothing is reindexed: a 180,000-message archive is not rebuilt on every search.
        Assert.Equal(0, second);
        var message = Assert.Single(result.Results);
        Assert.Equal("Archive", message.Folder);
        Assert.StartsWith("msg_", message.MessageRef);
    }

    [Fact]
    public void AccountCountsTrackTotalAndMessagesAddedSinceLastView()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Database.Upsert(SampleMessage("one", 1, "One", "First"));
        services.Database.Upsert(SampleMessage("two", 2, "Two", "Second"));

        Assert.Equal((2, 2), services.Database.AccountMessageCounts()["primary"]);
        services.Database.MarkSeen("primary", "INBOX");
        Assert.Equal((2, 0), services.Database.AccountMessageCounts()["primary"]);
        services.Database.Upsert(SampleMessage("three", 3, "Three", "Third"));
        Assert.Equal((3, 1), services.Database.AccountMessageCounts()["primary"]);
    }

    [Fact]
    public void StandardLocalFoldersAreSeededAndLocalQueriesExcludeConnectedMail()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        Assert.Equal([LocalFolderKinds.Inbox, LocalFolderKinds.Drafts, LocalFolderKinds.Outbox,
            LocalFolderKinds.Sent, LocalFolderKinds.Deleted], services.Database.ListLocalFolders().Select(folder => folder.Key));

        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local", LocalFolderKinds.Inbox, "local-1", 1, 0,
            "Local", "sender@example.invalid", "Sender", ["local@example.invalid"], DateTimeOffset.UtcNow,
            100, false, 0, "Local body", null, null));
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "connected", "Inbox", "connected-1", 2, 0,
            "Connected", "sender@example.invalid", "Sender", ["connected@example.invalid"], DateTimeOffset.UtcNow,
            100, false, 0, "Connected body", null, null));

        var local = services.Database.ListMessagesForAccounts(["local"], LocalFolderKinds.Inbox);
        Assert.Single(local);
        Assert.Equal("Local", local[0].Subject);
    }

    [Fact]
    public void OAuthScopesMatchTheReceivingProtocol()
    {
        Assert.Equal(["https://mail.google.com/"], OAuthClient.ScopesFor("gmail", MailProtocols.Pop3));
        Assert.Equal(["offline_access", "https://outlook.office.com/POP.AccessAsUser.All"],
            OAuthClient.ScopesFor("outlook", MailProtocols.Pop3));
        Assert.Equal(["offline_access", "https://outlook.office.com/IMAP.AccessAsUser.All"],
            OAuthClient.ScopesFor("microsoft", MailProtocols.Imap));
        Assert.Equal(["offline_access", "https://outlook.office.com/SMTP.Send"],
            OAuthClient.ScopesFor("microsoft", null, true));
    }

    [Fact]
    public void PopUidlLedgerIsDurableAndIdempotent()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();

        services.Database.RememberPopUidl("primary", "uidl-one");
        services.Database.RememberPopUidl("primary", "uidl-one");
        services.Database.RememberPopUidl("other", "uidl-two");

        Assert.Equal(["uidl-one"], services.Database.PopSeen("primary"));
        Assert.Equal(["uidl-two"], services.Database.PopSeen("other"));
    }

    [Fact]
    public async Task PopCollectsExistingMailAndSubsequentArrivalsOnce()
    {
        await using var server = new FakePopServer(3, growAfterFirstConnection: true);
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([
            new MailAccount("local", "custom", "user@example.invalid", "INBOX", "Inbox", null, null, "secret",
                new KeepAliveSettings(), MailboxModes.LocalKeep,
                new IncomingServerSettings(MailProtocols.Pop3, "127.0.0.1", server.Port, MailSecurityModes.Auto))
        ]);

        var first = await services.Sync.SyncAsync("local", "test");
        var second = await services.Sync.SyncAsync("local", "test");
        var third = await services.Sync.SyncAsync("local", "test");

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.True(third.Ok, third.Error);
        Assert.Equal(1, first.NewCount);
        Assert.Equal(1, second.NewCount);
        Assert.Equal(0, third.NewCount);
        Assert.Equal(2, services.Database.ListMessages("local").Count);
        Assert.Equal(2, server.RetrieveCommands);
        Assert.Equal(0, server.DeleteCommands);
    }

    [Fact]
    public async Task OutboxQueuesExactMimeClaimsOnceAndRetainsSuccessfulSend()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity()]);
        var transport = new FakeOutgoingTransport();
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, transport);
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Queued subject", "Queued body");

        var queued = await sender.QueueAsync(draft.Id);
        var results = await sender.SendPendingAsync();

        Assert.NotNull(queued.RawPath);
        Assert.Single(results);
        Assert.True(results[0].Ok, results[0].Error);
        Assert.Equal($"{draft.Id}@mailkeep.local", Assert.Single(transport.Messages).MessageId);
        Assert.Equal(OutgoingStates.Sent, services.Database.GetOutgoing(draft.Id)?.Status);
        Assert.Contains(services.Database.ListMessages("sender"), message => message.Folder == LocalFolderKinds.Sent && message.Subject == "Queued subject");
        Assert.Single(services.Database.ListMessagesForAccounts(["sender"], LocalFolderKinds.Sent));
    }

    [Fact]
    public async Task AFormattedDraftGoesAsHtmlAndText()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity()]);
        var transport = new FakeOutgoingTransport();
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, transport);
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Digest", "Plain words",
            bodyHtml: "<html><body><b>Bold</b> words</body></html>");

        Assert.Equal("<html><body><b>Bold</b> words</body></html>", services.Database.GetOutgoing(draft.Id)?.BodyHtml);
        await sender.QueueAsync(draft.Id);
        var results = await sender.SendPendingAsync();

        Assert.True(results[0].Ok, results[0].Error);
        var (text, html) = Assert.Single(transport.Bodies);
        Assert.Contains("<b>Bold</b>", html);
        Assert.Equal("Plain words", text);
    }

    [Fact]
    public async Task FailedOutboxSendReturnsToRetryWithoutASecondConcurrentClaim()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity()]);
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs,
            new FakeOutgoingTransport(new IOException("offline")));
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Retry subject", "Retry body");
        await sender.QueueAsync(draft.Id);

        var claimed = services.Database.ClaimNextOutgoing();
        var duplicate = services.Database.ClaimNextOutgoing();
        services.Database.MarkOutgoingFailed(claimed!.Id, "first failure");

        Assert.Null(duplicate);
        var failed = services.Database.GetOutgoing(draft.Id)!;
        Assert.Equal(OutgoingStates.Outbox, failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.NotNull(failed.NextAttemptAt);
    }

    [Fact]
    public async Task SmtpWireHarnessRetriesConnectionLimitAndDeliversOnce()
    {
        await using var smtp = new SmtpWireHarness(limitFirstConnection: true);
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity(smtp.Port)]);
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs,
            new SmtpOutgoingTransport(services.OAuth));
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Retry once", "fixture");
        await sender.QueueAsync(draft.Id);
        Assert.True(Assert.Single(await sender.SendQueuedAsync([draft.Id])).Ok);
        Assert.Single(smtp.Messages);
    }

    [Fact]
    public async Task SmtpWireHarnessDeliversOnceAndRetainsSentCopy()
    {
        await using var smtp = new SmtpWireHarness();
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity(smtp.Port)]);
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs,
            new SmtpOutgoingTransport(services.OAuth));
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Wire subject", "Wire body");
        await sender.QueueAsync(draft.Id);

        var first = await sender.SendPendingAsync();
        var second = await sender.SendPendingAsync();
        await smtp.Completion;

        Assert.True(Assert.Single(first).Ok);
        Assert.Empty(second);
        Assert.Equal("sender@example.invalid", smtp.Username);
        Assert.Equal("secret", smtp.Password);
        var delivered = Assert.Single(smtp.Messages);
        Assert.Contains("Subject: Wire subject", delivered);
        Assert.Contains("Wire body", delivered);
        Assert.Equal(OutgoingStates.Sent, services.Database.GetOutgoing(draft.Id)?.Status);
        Assert.Single(services.Database.ListMessages("sender"), message => message.Folder == LocalFolderKinds.Sent);
    }

    [Fact]
    public async Task SmtpWireHarnessRejectionReturnsMessageToOutboxRetry()
    {
        await using var smtp = new SmtpWireHarness(rejectData: true);
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity(smtp.Port)]);
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs,
            new SmtpOutgoingTransport(services.OAuth));
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Rejected subject", "Rejected body");
        await sender.QueueAsync(draft.Id);

        var result = Assert.Single(await sender.SendPendingAsync());
        await smtp.Completion;

        Assert.False(result.Ok);
        var queued = services.Database.GetOutgoing(draft.Id)!;
        Assert.Equal(OutgoingStates.Outbox, queued.Status);
        Assert.Equal(1, queued.AttemptCount);
        Assert.Contains("rejected", queued.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(services.Database.ListMessages("sender"), message => message.Folder == LocalFolderKinds.Sent);
    }

    [Fact]
    public async Task IdentityTestAuthenticatesWithoutSendingAndOnlyThenUnlocksQueueing()
    {
        await using var smtp = new SmtpWireHarness();
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity(smtp.Port) with { Status = SendingIdentityStates.NeedsAuthorization }]);
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs,
            new SmtpOutgoingTransport(services.OAuth));
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Blocked subject", "Blocked body");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.QueueAsync(draft.Id));
        var verdict = await sender.TestAsync("sender");
        await smtp.Completion;

        Assert.True(verdict.Ok, verdict.Error);
        Assert.Empty(smtp.Messages);
        Assert.Equal("sender@example.invalid", smtp.Username);
        Assert.Equal(SendingIdentityStates.Ready, services.Identities.Require("sender").Status);
        Assert.Null(services.Identities.Require("sender").LastTestError);
        Assert.Equal(OutgoingStates.Outbox, (await sender.QueueAsync(draft.Id)).Status);
    }

    [Fact]
    public async Task FailedIdentityTestRecordsTheProviderErrorAndKeepsSendingBlocked()
    {
        await using var smtp = new SmtpWireHarness(rejectAuth: true);
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity(smtp.Port) with { Status = SendingIdentityStates.NeedsAuthorization }]);
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs,
            new SmtpOutgoingTransport(services.OAuth));
        var draft = sender.SaveDraft("sender", ["recipient@example.invalid"], "Still blocked", "Still blocked");

        var verdict = await sender.TestAsync("sender");

        Assert.False(verdict.Ok);
        Assert.Equal(SendingIdentityStates.FailedTest, services.Identities.Require("sender").Status);
        Assert.False(string.IsNullOrWhiteSpace(services.Identities.Require("sender").LastTestError));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.QueueAsync(draft.Id));
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task UntestedIdentityWithNoCredentialCannotBeMarkedReady()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity() with { Password = null, Status = SendingIdentityStates.Ready }]);
        var transport = new FakeOutgoingTransport();
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, transport);

        var verdict = await sender.TestAsync("sender");

        Assert.False(verdict.Ok);
        Assert.Equal(0, transport.Verifications);
        Assert.Equal(SendingIdentityStates.NeedsAuthorization, services.Identities.Require("sender").Status);
        Assert.False(services.Identities.Require("sender").IsReady);
    }

    [Fact]
    public void ChangingWhereOrHowAnIdentityAuthenticatesInvalidatesItsLastTest()
    {
        var tested = OutgoingIdentity();

        Assert.Equal(SendingIdentityStates.Ready,
            SendingIdentityStore.ResetTestIfConnectionChanged(tested, tested with { DisplayName = "Renamed" }).Status);
        Assert.Equal(SendingIdentityStates.NeedsAuthorization,
            SendingIdentityStore.ResetTestIfConnectionChanged(tested, tested with { Password = "rotated" }).Status);
        Assert.Equal(SendingIdentityStates.NeedsAuthorization,
            SendingIdentityStore.ResetTestIfConnectionChanged(tested, tested with { Smtp = tested.Smtp with { Port = 2525 } }).Status);
        Assert.Equal(SendingIdentityStates.NeedsAuthorization,
            SendingIdentityStore.ResetTestIfConnectionChanged(tested, tested with { Username = "other@example.invalid" }).Status);
    }

    [Fact]
    public void AnIdentityKeepsItsAuthorizationWhenTheCredentialIsUnchangedButReloaded()
    {
        // Every save reloads identities from disk, so the credential arrives as a fresh object with the same
        // contents. Comparing those by reference made an untouched identity look re-credentialed, and merely
        // choosing a default From address silently revoked the authorization behind it.
        var credential = new OAuthCredential("client", "secret", "refresh", ["scope.send"]);
        var tested = OutgoingIdentity() with { OAuth = credential, Password = null, Status = SendingIdentityStates.Ready };
        var reloaded = tested with { OAuth = new OAuthCredential("client", "secret", "refresh", ["scope.send"]) };

        Assert.Equal(SendingIdentityStates.Ready,
            SendingIdentityStore.ResetTestIfConnectionChanged(tested, reloaded).Status);
        Assert.Equal(SendingIdentityStates.Ready,
            SendingIdentityStore.ResetTestIfConnectionChanged(tested, reloaded with { IsDefault = !tested.IsDefault }).Status);
        // A genuinely rotated refresh token still has to be re-tested.
        Assert.Equal(SendingIdentityStates.NeedsAuthorization,
            SendingIdentityStore.ResetTestIfConnectionChanged(tested,
                tested with { OAuth = credential with { RefreshToken = "rotated" } }).Status);
    }

    [Fact]
    public void ExactlyOneSendingIdentityIsTheDefaultThroughEveryWrite()
    {
        using var temp = new TempDirectory();
        var store = new SendingIdentityStore(new AppPaths(temp.Path));
        var first = OutgoingIdentity() with { Id = "alpha", Email = "alpha@example.invalid", IsDefault = true };
        var second = OutgoingIdentity() with { Id = "beta", Email = "beta@example.invalid", IsDefault = true };

        store.Save([first, second]);
        Assert.Equal("alpha", Assert.Single(store.Load(), identity => identity.IsDefault).Id);

        store.Upsert(second);
        Assert.Equal("beta", Assert.Single(store.Load(), identity => identity.IsDefault).Id);

        store.SetDefault("alpha");
        Assert.Equal("alpha", Assert.Single(store.Load(), identity => identity.IsDefault).Id);

        Assert.True(store.Remove("alpha"));
        Assert.False(store.Remove("alpha"));
        Assert.Equal("beta", Assert.Single(store.Load()).Id);
        Assert.True(store.Load()[0].IsDefault);
    }

    [Fact]
    public async Task DraftsAreDiscardableUntilTheyAreQueued()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Identities.Save([OutgoingIdentity()]);
        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, new FakeOutgoingTransport());
        var discarded = sender.SaveDraft("sender", ["recipient@example.invalid"], "Discard me", "Body");
        var queued = sender.SaveDraft("sender", ["recipient@example.invalid"], "Keep me", "Body");
        await sender.QueueAsync(queued.Id);

        Assert.True(sender.DiscardDraft(discarded.Id));
        Assert.Null(services.Database.GetOutgoing(discarded.Id));
        Assert.False(sender.DiscardDraft(queued.Id));
        Assert.Equal(OutgoingStates.Outbox, services.Database.GetOutgoing(queued.Id)?.Status);
    }

    [Fact]
    public void AccountStoreRoundTripsThroughCurrentUserDpapi()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new AccountStore(paths);
        var account = new MailAccount("primary", "gmail", "user@example.invalid", "INBOX", "Primary", null,
            new OAuthCredential("client", "secret", "refresh"), null, new KeepAliveSettings(true, 24));

        store.Save([account]);
        var loaded = Assert.Single(store.Load());

        Assert.Equal(account.Name, loaded.Name);
        Assert.Equal(account.OAuth, loaded.OAuth);
        Assert.Equal(MailboxModes.Connected, loaded.MailboxMode);
        Assert.Equal(MailProtocols.Imap, loaded.Incoming?.Protocol);
        Assert.Equal("imap.gmail.com", loaded.Incoming?.Host);
        Assert.Equal(993, loaded.Incoming?.Port);
        Assert.DoesNotContain("refresh", Encoding.UTF8.GetString(File.ReadAllBytes(paths.AccountsPath)));
    }

    [Fact]
    public void IncomingAccountsCannotActAsSendingIdentities()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Accounts.Save([new MailAccount("incoming", "gmail", "incoming@example.invalid", "INBOX", "Inbox", null, null, null, new KeepAliveSettings())]);

        var sender = new MailSendService(services.Identities, services.Database, services.Blobs, new FakeOutgoingTransport());
        Assert.Throws<KeyNotFoundException>(() => sender.SaveDraft("incoming", ["recipient@example.invalid"], "Subject", "Body"));
    }

    [Fact]
    public void MailboxModeMigrationDefaultsOnlyUnclassifiedAccountsToConnected()
    {
        using var temp = new TempDirectory();
        var store = new AccountStore(new AppPaths(temp.Path));
        store.Save([
            new MailAccount("old", "gmail", "old@example.invalid", "INBOX", "Old", null, null, null, new KeepAliveSettings()),
            new MailAccount("local", "gmail", "local@example.invalid", "INBOX", "Local", null, null, null,
                new KeepAliveSettings(), MailboxModes.LocalKeep)
        ]);

        Assert.Equal(0, store.EnsureMailboxModes());
        var accounts = store.Load().ToDictionary(account => account.Name);
        Assert.Equal(MailboxModes.Connected, accounts["old"].MailboxMode);
        Assert.Equal(MailboxModes.LocalKeep, accounts["local"].MailboxMode);
    }

    [Fact]
    public void ConnectedAccountsRejectPopButLocalMailAcceptsIt()
    {
        using var temp = new TempDirectory();
        var store = new AccountStore(new AppPaths(temp.Path));
        var pop = new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995);
        var connected = new MailAccount("connected", "custom", "user@example.invalid", "INBOX", "Connected",
            null, null, "secret", new KeepAliveSettings(), MailboxModes.Connected, pop);

        var error = Assert.Throws<InvalidDataException>(() => store.Save([connected]));
        Assert.Contains("Connected Accounts require IMAP", error.Message);

        store.Save([connected with { Name = "local", MailboxMode = MailboxModes.LocalKeep }]);
        Assert.Equal(MailProtocols.Pop3, Assert.Single(store.Load()).Incoming?.Protocol);
    }

    [Fact]
    public void SameAddressCanBeConnectedAndLocalButCannotDuplicateAMode()
    {
        using var temp = new TempDirectory();
        var store = new AccountStore(new AppPaths(temp.Path));
        var connected = new MailAccount("connected", "gmail", "user@example.invalid", "INBOX", "Inbox",
            null, null, "secret", new KeepAliveSettings(), MailboxModes.Connected,
            new IncomingServerSettings(MailProtocols.Imap, "imap.example.invalid", 993));
        var local = connected with
        {
            Name = "local", MailboxMode = MailboxModes.LocalKeep, FolderLabel = LocalFolderKinds.Inbox,
            Incoming = new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995)
        };
        store.Save([connected, local]);
        Assert.Equal(2, store.Load().Count);

        var duplicateLocal = local with { Name = "local-copy" };
        Assert.Throws<InvalidDataException>(() => store.Save([connected, local, duplicateLocal]));
    }


    [Fact]
    public async Task DeleteMovesTheLocalCopyToDeletedAndSuppressesTheSameMessageOnResync()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Accounts.Save([
            new MailAccount("primary", "gmail", "user@example.invalid", "INBOX", "Primary",
                "imap.gmail.com", null, null, new KeepAliveSettings(), MailboxModes.LocalKeep)
        ]);
        var input = SampleMessage("1:88", 88, "Keep on provider", "Local copy");
        var id = services.Database.Upsert(input);

        await services.Sync.DeleteAsync(id);
        var restoredId = services.Database.Upsert(input);

        var deleted = Assert.Single(services.Database.ListMessages());
        Assert.Equal(LocalFolderKinds.Deleted, deleted.Folder);
        Assert.Null(deleted.ServerUid);
        Assert.Equal(0, restoredId);
    }

    /// <summary>What a sync costs afterwards. A message arriving used to change the archive's generation, and
    /// a changed generation meant reading every archived message back out with its body and writing the whole
    /// index again — twenty minutes of it behind a fifty-second sync. Only what changed is indexed.</summary>
    [Fact]
    public void OnlyTheArchivedMailThatChangedIsIndexedAgain()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        for (var index = 0; index < 5; index++)
            services.Database.Upsert(Archived("held-" + index, "Note " + index, "A body worth keeping"));
        Assert.Equal(5, services.Catalog.SyncArchive());

        services.Database.Upsert(Archived("newcomer", "Newcomer", "A body worth keeping"));

        Assert.Equal(1, services.Catalog.SyncArchive());
        // The five already indexed are still there, so the one pass indexed the newcomer and nothing else.
        Assert.Equal(6, services.Catalog.Search("keeping", MailContract.ArchiveSource, 20).Count);

        // A message that has left the store leaves the index with it, rather than being found forever.
        services.Database.RemoveFromFolder(MailContract.ArchiveSource, "OE Classic", "Archive", "newcomer");
        Assert.Equal(0, services.Catalog.SyncArchive());
        Assert.Equal(5, services.Catalog.Search("keeping", MailContract.ArchiveSource, 20).Count);
        Assert.Empty(services.Catalog.Search("newcomer", MailContract.ArchiveSource, 10).Results);
    }

    private static MessageInput Archived(string key, string subject, string body)
        => SampleMessage(key, null, subject, body)
            with { Source = MailContract.ArchiveSource, Account = "OE Classic", Folder = "Archive" };

    [Fact]
    public void SearchFollowsFilingAndReadChangesWithoutNewMail()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var id = services.Database.Upsert(SampleMessage("filing", 1, "Filing fixture", "filing marigold"));
        services.Catalog.SyncOperational();
        Assert.Single(services.Catalog.Search("marigold", folder: "Primary").Results);
        var target = services.Database.CreateLocalFolder("Filed", null);
        services.Database.MoveMessage(id, target.Key);
        services.Catalog.SyncOperational();
        Assert.Empty(services.Catalog.Search("marigold", folder: "Primary").Results);
        Assert.Single(services.Catalog.Search("marigold", folder: target.Key).Results);
        services.Database.SetReadState(MailContract.LiveSource, "primary", target.Key, "filing", true);
        services.Catalog.SyncOperational();
        Assert.True(Assert.Single(services.Catalog.Search("marigold").Results).IsRead);
        Assert.Equal(0, services.Catalog.SyncOperational());
    }

    [Fact]
    public void CancelledArchiveReplacementRollsBackAndCanBeRetried()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        var original = new CatalogDocument(MailContract.ArchiveSource, "fixture", "Archive", "original", null,
            "Original", "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow, 1, false, 0, "original marigold");
        services.Catalog.ReplaceArchive([original], "before");
        using var cancellation = new CancellationTokenSource();
        IEnumerable<CatalogDocument> Replacement()
        {
            yield return original with { SourceKey = "replacement", BodyText = "replacement orchid" };
            cancellation.Cancel();
            yield return original with { SourceKey = "second" };
        }
        Assert.ThrowsAny<OperationCanceledException>(() => services.Catalog.ReplaceArchive(Replacement(), "after",
            cancellationToken: cancellation.Token));
        Assert.Single(services.Catalog.Search("marigold").Results);
        Assert.Empty(services.Catalog.Search("orchid").Results);
        Assert.Equal("before", Assert.Single(services.Catalog.Status()).Generation);
        Assert.Equal(1, services.Catalog.ReplaceArchive([original with { BodyText = "replacement orchid" }], "after"));
        Assert.Single(services.Catalog.Search("orchid").Results);
    }

    [Fact]
    public void CancelledIncrementalArchiveSyncKeepsPreviousGeneration()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Database.Upsert(Archived("held", "Held", "marigold"));
        services.Catalog.SyncArchive();
        var generation = Assert.Single(services.Catalog.Status()).Generation;
        services.Database.Upsert(Archived("new", "New", "orchid"));
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => services.Catalog.SyncArchive(progress: progress =>
        {
            if (progress.Stage == "committing") cancellation.Cancel();
        }, cancellationToken: cancellation.Token));
        Assert.Equal(generation, Assert.Single(services.Catalog.Status()).Generation);
        Assert.Empty(services.Catalog.Search("orchid").Results);
        Assert.Equal(1, services.Catalog.SyncArchive());
        Assert.Single(services.Catalog.Search("orchid").Results);
    }

    [Fact]
    public void PrecancelledSearchAndSyncDoNotStart()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => services.Catalog.Search("fixture", cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => services.Catalog.SyncOperational(cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => services.Catalog.SyncArchive(cancellationToken: cancellation.Token));
        Assert.False(File.Exists(services.Paths.SearchCatalogPath));
    }

    [Theory]
    [InlineData("Budget%", "Budget2026")]
    [InlineData("Budget_", "BudgetX")]
    [InlineData("Budget\\", "Budget")]
    public void SubfolderSearchTreatsFolderNamesLiterally(string folder, string similar)
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        CatalogDocument Document(string key, string location) => new(MailContract.ArchiveSource, "fixture", location,
            key, null, key, "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow, 1, false, 0, "scope marigold");
        services.Catalog.ReplaceArchive([
            Document("parent", folder), Document("child", folder + "/Child"),
            Document("sibling", similar + "/Child"), Document("prefix", folder + "Extra/Child")
        ], "scope");
        Assert.Equal(new[] { "child", "parent" }, services.Catalog.Search("marigold", folder: folder,
            filters: new SearchFilters(IncludeSubfolders: true)).Results.Select(hit => hit.Subject).Order());
        Assert.Equal("parent", Assert.Single(services.Catalog.Search("marigold", folder: folder).Results).Subject);
        Assert.Equal("parent", Assert.Single(services.Catalog.Search($"folder:\"{folder}\"").Results).Subject);
        var keyScope = new SearchFilters(FolderKeys: [folder, folder + "/Child"]);
        Assert.Equal(new[] { "child", "parent" }, services.Catalog.Search("marigold", filters: keyScope).Results.Select(hit => hit.Subject).Order());
        Assert.Equal(new[] { "child", "parent" }, services.Catalog.Search("", filters: keyScope).Results.Select(hit => hit.Subject).Order());
        Assert.Empty(services.Catalog.Search("marigold", filters: new SearchFilters(FolderKeys: [])).Results);
    }

    [Fact]
    public void DateRangeIncludesItsStartAndExcludesTheNextDayBoundary()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        var start = DateTimeOffset.Parse("2026-09-06T00:00:00+01:00");
        var end = start.AddDays(1);
        CatalogDocument Document(string key, DateTimeOffset? date, int attachments = 0) => new(
            MailContract.ArchiveSource, "fixture", "Archive", key, null, key, "sender@example.invalid", "Sender", [],
            date, 1, false, attachments, "date marigold");
        services.Catalog.ReplaceArchive([
            Document("before", start.AddTicks(-1)), Document("start", start),
            Document("last", end.AddTicks(-1), 1), Document("end", end), Document("undated", null)
        ], "dates");
        var filters = new SearchFilters(After: start, Before: end);
        Assert.Equal(new[] { "last", "start" }, services.Catalog.Search("marigold", filters: filters)
            .Results.Select(hit => hit.Subject).Order());
        Assert.Equal("last", Assert.Single(services.Catalog.Search("marigold", filters: filters with { RequireAttachment = true }).Results).Subject);
        Assert.Equal(new[] { "last", "start" }, services.Catalog.Search("", filters: filters)
            .Results.Select(hit => hit.Subject).Order());
        Assert.Equal("last", Assert.Single(services.Catalog.Search("", filters: new SearchFilters(RequireAttachment: true)).Results).Subject);
        Assert.Empty(services.Catalog.Search("").Results);
    }

    /// <summary>A listing must not drag the bodies along with it. Taking even a snippet of body_text made
    /// SQLite read every message's body out of the pages it spills into, which is what made opening a large
    /// folder slow; nothing in a list shows it. The message itself still carries one, having the body anyway.</summary>
    [Fact]
    public void ListingCarriesNoBodyButAnOpenedMessageStillCarriesItsSnippet()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var id = services.Database.Upsert(SampleMessage("only", 1, "Quarterly invoice", "The body of the message"));

        var listed = Assert.Single(services.Database.ListMessages());
        Assert.Equal("", listed.Snippet);
        Assert.Equal("Quarterly invoice", listed.Subject);

        var opened = services.Database.GetMessage(id);
        Assert.NotNull(opened);
        Assert.Equal("The body of the message", opened.Summary.Snippet);
    }

    private static MessageInput SampleMessage(string key, long? uid, string subject, string body) => new(
        MailContract.LiveSource, "primary", "Primary", key, uid, 1, subject,
        "sender@example.invalid", "Sender", ["recipient@example.invalid"],
        DateTimeOffset.Parse("2026-08-01T10:00:00Z"), 123, false, 0, body, null, null);

    private static SendingIdentity OutgoingIdentity(int smtpPort = 587) => new("sender", null, "Sender", "sender@example.invalid", null,
        false, "local", new OutgoingServerSettings(smtpPort == 587 ? "smtp.example.invalid" : "127.0.0.1", smtpPort, MailSecurityModes.Auto),
        null, null, "secret", SendingIdentityStates.Ready);

    private static void WriteLegacyAccounts(string root)
    {
        Directory.CreateDirectory(root);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            accounts = new[]
            {
                new
                {
                    name = "primary", provider = "gmail", address = "user@example.invalid", mailbox = "INBOX",
                    folder_label = "Primary", host = "imap.gmail.com",
                    oauth = new { client_id = "client", client_secret = "secret", refresh_token = "refresh" },
                    keepalive = new { enabled = true, interval_hours = 48 }
                }
            }
        });
        File.WriteAllBytes(System.IO.Path.Combine(root, "accounts.dpapi"), WindowsDpapi.Protect(payload));
    }

    private static void WriteLegacyDatabase(string root)
    {
        var data = System.IO.Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        using var connection = new SqliteConnection($"Data Source={System.IO.Path.Combine(data, "mailkeep.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE emails(rowid INTEGER PRIMARY KEY,folder TEXT,email_id INTEGER,subject TEXT,sender TEXT,recipient TEXT,date_iso TEXT,size INTEGER);
            CREATE VIRTUAL TABLE emails_fts USING fts5(subject,sender,recipient,body_text);
            CREATE TABLE email_html(rowid INTEGER PRIMARY KEY,html TEXT);
            CREATE TABLE folder_state(account TEXT PRIMARY KEY,folder_label TEXT,uidvalidity INTEGER,max_uid INTEGER,last_fetch_at TEXT,seen_max_rowid INTEGER);
            CREATE TABLE fetch_log(id INTEGER PRIMARY KEY,account TEXT,address TEXT,source TEXT,started_at TEXT,finished_at TEXT,duration_ms INTEGER,new_count INTEGER,scanned INTEGER,max_uid INTEGER,ok INTEGER,error TEXT);
            INSERT INTO emails VALUES(1,'Primary',42,'Legacy subject','sender@example.invalid','recipient@example.invalid','2026-07-01T09:00:00Z',321);
            INSERT INTO emails_fts(rowid,subject,sender,recipient,body_text) VALUES(1,'Legacy subject','sender@example.invalid','recipient@example.invalid','Legacy body');
            INSERT INTO email_html VALUES(1,'<p>Legacy body</p>');
            INSERT INTO folder_state VALUES('primary','Primary',77,42,'2026-07-01T09:05:00Z',1);
            INSERT INTO fetch_log VALUES(1,'primary','user@example.invalid','scheduler','2026-07-01T09:00:00Z','2026-07-01T09:00:01Z',1000,1,1,42,1,NULL);
            """;
        command.ExecuteNonQuery();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailkeep-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FakePopServer : IAsyncDisposable
    {
        private const string Message = "From: Sender <sender@example.invalid>\r\nTo: user@example.invalid\r\nSubject: POP fixture\r\nDate: Sat, 01 Aug 2026 10:00:00 +0000\r\nMessage-Id: <pop-fixture@example.invalid>\r\n\r\nFixture body\r\n";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serverTask;
        private int _deleteCommands;
        private int _retrieveCommands;

        private readonly bool _grow;

        public FakePopServer(int connections, bool growAfterFirstConnection = false)
        {
            _grow = growAfterFirstConnection;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverTask = RunAsync(connections);
        }

        public int Port { get; }
        public int DeleteCommands => _deleteCommands;
        public int RetrieveCommands => _retrieveCommands;

        private async Task RunAsync(int connections)
        {
            for (var connection = 0; connection < connections; connection++)
            {
                // A growing mailbox: one message exists at first contact, a second "arrives" afterwards.
                var messageCount = _grow && connection > 0 ? 2 : 1;
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("+OK Mailkeep test POP3");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var command = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (command)
                    {
                        case "CAPA":
                            await writer.WriteAsync("+OK\r\nUIDL\r\nUSER\r\n.\r\n");
                            break;
                        case "USER":
                        case "PASS":
                            await writer.WriteLineAsync("+OK");
                            break;
                        case "STAT":
                            await writer.WriteLineAsync($"+OK {messageCount} {Message.Length * messageCount}");
                            break;
                        case "UIDL":
                            await writer.WriteAsync("+OK\r\n" + string.Join("", Enumerable.Range(1, messageCount)
                                .Select(number => $"{number} uidl-{number}\r\n")) + ".\r\n");
                            break;
                        case "RETR":
                            Interlocked.Increment(ref _retrieveCommands);
                            await writer.WriteAsync($"+OK {Message.Length}\r\n{Message}.\r\n");
                            break;
                        case "DELE":
                            Interlocked.Increment(ref _deleteCommands);
                            await writer.WriteLineAsync("+OK");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("+OK");
                            goto NextConnection;
                        default:
                            await writer.WriteLineAsync("-ERR unsupported");
                            break;
                    }
                }
            NextConnection:;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serverTask;
        }
    }

    private sealed class FakeOutgoingTransport(Exception? error = null) : IOutgoingTransport
    {
        public List<MimeKit.MimeMessage> Messages { get; } = [];
        // Read at send time: the service disposes the message afterwards, and a disposed body cannot be read.
        public List<(string? Text, string? Html)> Bodies { get; } = [];
        public int Verifications { get; private set; }

        public Task SendAsync(SendingIdentity identity, MimeKit.MimeMessage message, CancellationToken cancellationToken = default)
        {
            if (error is not null) throw error;
            Messages.Add(message);
            Bodies.Add((message.TextBody, message.HtmlBody));
            return Task.CompletedTask;
        }

        public Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default)
        {
            Verifications++;
            if (error is not null) throw error;
            return Task.CompletedTask;
        }
    }

    private sealed class SmtpWireHarness : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _rejectData;
        private readonly bool _rejectAuth;
        private readonly Task _serverTask;

        public SmtpWireHarness(bool rejectData = false, bool rejectAuth = false, bool limitFirstConnection = false)
        {
            _rejectData = rejectData;
            _rejectAuth = rejectAuth;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverTask = ServeAsync(limitFirstConnection);
        }

        public int Port { get; }
        public string? Username { get; private set; }
        public string? Password { get; private set; }
        public List<string> Messages { get; } = [];
        public Task Completion => _serverTask;

        private async Task ServeAsync(bool limitFirstConnection)
        {
            if (limitFirstConnection) await RunAsync(true);
            await RunAsync(false);
        }

        private async Task RunAsync(bool limited)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 mailkeep.local ESMTP harness");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var command = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (command)
                    {
                        case "EHLO":
                        case "HELO":
                            await writer.WriteAsync("250-mailkeep.local\r\n250-AUTH PLAIN LOGIN\r\n250 SIZE 10485760\r\n");
                            break;
                        case "AUTH":
                            if (_rejectAuth) { await writer.WriteLineAsync("535 5.7.8 authentication failed"); break; }
                            Authenticate(line);
                            await writer.WriteLineAsync("235 2.7.0 authenticated");
                            break;
                        case "MAIL":
                            if (limited) { await writer.WriteLineAsync("451 4.3.2 Concurrent connections limit exceeded"); return; }
                            goto case "RCPT";
                        case "RCPT":
                            await writer.WriteLineAsync("250 2.1.0 accepted");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>");
                            var message = await ReadDataAsync(reader);
                            Messages.Add(message);
                            await writer.WriteLineAsync(_rejectData ? "451 4.3.0 message rejected by harness" : "250 2.0.0 queued");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 2.0.0 goodbye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 5.5.2 unsupported command");
                            break;
                    }
                }
            }
            catch (SocketException) when (!_listener.Server.IsBound)
            {
            }
        }

        private void Authenticate(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Harness expected AUTH PLAIN with an initial response.");
            var values = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])).Split('\0');
            Username = values[^2];
            Password = values[^1];
        }

        private static async Task<string> ReadDataAsync(StreamReader reader)
        {
            var lines = new List<string>();
            while (await reader.ReadLineAsync() is { } line && line != ".")
                lines.Add(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
            return string.Join("\r\n", lines);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _serverTask; }
            catch (ObjectDisposedException) { }
        }
    }
}
