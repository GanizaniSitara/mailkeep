using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class FtsQueryTests
{
    [Theory]
    [InlineData("quarterly invoice", "\"quarterly\" AND \"invoice\"")]
    [InlineData("\"quarterly invoice\"", "\"quarterly invoice\"")]
    [InlineData("ship*", "\"ship\"*")]
    [InlineData("subject:invoice", "subject : \"invoice\"")]
    [InlineData("subject:\"quarterly invoice\"", "subject : \"quarterly invoice\"")]
    [InlineData("sender:amazon OR sender:ebay", "sender : \"amazon\" OR sender : \"ebay\"")]
    [InlineData("to:me@example.invalid", "recipients : \"me@example.invalid\"")]
    [InlineData("invoice NOT amazon", "\"invoice\" NOT \"amazon\"")]
    // Lowercase operators are just words; unknown prefixes are literal text.
    [InlineData("cats or dogs", "\"cats\" AND \"or\" AND \"dogs\"")]
    [InlineData("re:something", "\"re:something\"")]
    // Operator debris is dropped, never passed through to FTS5.
    [InlineData("AND invoice", "\"invoice\"")]
    [InlineData("invoice AND", "\"invoice\"")]
    [InlineData("invoice AND OR amazon", "\"invoice\" AND \"amazon\"")]
    public void TranslatesWhatPeopleType(string query, string expected)
        => Assert.Equal(expected, FtsQuery.Parse(query).Match);

    [Fact]
    public void FolderAndAccountLeaveTheMatchAndComeBackSeparately()
    {
        var translated = FtsQuery.Parse("folder:\"Archive/Receipts\" account:\"OE Classic\" invoice");
        Assert.Equal("\"invoice\"", translated.Match);
        Assert.Equal("Archive/Receipts", translated.Folder);
        Assert.Equal("OE Classic", translated.Account);
    }

    [Theory]
    [InlineData("\"unclosed phrase")]
    [InlineData("c++ email@addr.invalid")]
    [InlineData("((( AND")]
    [InlineData("*")]
    public void NothingAPersonTypesThrows(string query)
        => _ = FtsQuery.Parse(query);

    [Fact]
    public void SearchUnderstandsTheQueryLanguageEndToEnd()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        services.Database.Upsert(Message("one", "Quarterly invoice", "billing@shop.invalid", "The quarterly amount is due"));
        services.Database.Upsert(Message("two", "Invoice reminder", "billing@shop.invalid", "A reminder about your invoice"));
        services.Database.Upsert(Message("three", "Holiday plans", "friend@example.invalid", "Quarterly is a strange word for holidays"));
        services.Catalog.SyncOperational(force: true);

        // A phrase narrows; the same words unquoted match more.
        Assert.Equal(1, services.Catalog.Search("\"quarterly invoice\"").Count);
        Assert.Equal(2, services.Catalog.Search("quarterly").Count);
        // Column filters bind to their column only.
        Assert.Equal(1, services.Catalog.Search("subject:quarterly").Count);
        Assert.Equal(2, services.Catalog.Search("sender:billing*").Count);
        // OR widens; NOT excludes.
        Assert.Equal(3, services.Catalog.Search("invoice OR holiday").Count);
        Assert.Equal(1, services.Catalog.Search("quarterly NOT invoice").Count);
        // Garbage still answers instead of throwing.
        _ = services.Catalog.Search("\"((( AND c++");
        _ = services.Catalog.Search("*");
    }

    private static MessageInput Message(string key, string subject, string sender, string body) => new(
        MailContract.LiveSource, "primary", "Inbox", key, null, 0, subject, sender, "", ["me@example.invalid"],
        DateTimeOffset.Parse("2026-08-01T10:00:00Z"), 100, true, 0, body, null, null);

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailkeep-fts-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
