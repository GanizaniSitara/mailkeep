using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class UnreadMessagesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-unread-" + Guid.NewGuid());
    private readonly MailkeepServices services;
    public UnreadMessagesTests()
    {
        services = new MailkeepServices(root);
        services.Database.Initialize();
    }

    private long Add(string key, bool read = false, string source = MailContract.LiveSource,
        string folder = "Inbox", int day = 1) => services.Database.Upsert(new MessageInput(
            source, "fixture", folder, key, null, 0, key, "sender@example.invalid", "Sender", [],
            new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero), 123, read, 0,
            "Private body must not be returned", null, null));

    [Fact]
    public void ReturnsNewestUnreadLiveMessagesWithoutChangingReadState()
    {
        var old = Add("old");
        var newest = Add("newest", day: 3);
        Add("already read", read: true, day: 4);
        Add("imported archive", source: MailContract.ArchiveSource, day: 5);
        Add("deleted", folder: LocalFolderKinds.Deleted, day: 6);
        var result = services.Database.ListUnreadMessages(10);
        Assert.Equal(new[] { newest, old }, result.Select(m => m.Id));
        Assert.All(result, m => Assert.False(m.IsRead));
        Assert.All(result, m => Assert.Empty(m.Snippet));
        Assert.False(services.Database.GetMessage(newest)!.Summary.IsRead);
        Assert.False(services.Database.GetMessage(old)!.Summary.IsRead);
    }

    [Fact]
    public void LimitAndEmptyResultsAreReliable()
    {
        Assert.Empty(services.Database.ListUnreadMessages(10));
        Add("one"); Add("two", day: 2);
        Assert.Single(services.Database.ListUnreadMessages(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.Database.ListUnreadMessages(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.Database.ListUnreadMessages(22));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
