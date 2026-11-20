using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Tests;

public sealed class SenderSearchTests
{
    [Theory]
    [InlineData("harbourton")]
    [InlineData("fielding")]
    [InlineData("bourtonfiel")]
    [InlineData("BOURTON")]
    [InlineData("@harb")]
    [InlineData("Har")]
    [InlineData("Name Only")]
    public void FromFindsLiteralFragmentsInAddressOrDisplayName(string fragment)
    {
        using var temp = new Store();
        temp.Catalog.ReplaceArchive([
            Document("wanted", "helpdesk@harbourtonfielding.invalid", "Name Only"),
            Document("other", "other@example.invalid", "Other") with { BodyText = "harbourton fielding Name Only" }
        ], "one");
        var result = temp.Catalog.Search("", filters: new SearchFilters(Sender: fragment));
        Assert.Equal("wanted", Assert.Single(result.Results).Subject);
    }

    [Theory]
    [InlineData("h")]
    [InlineData("ha")]
    public void ShortFromDoesNotRunAnUnboundedPartialSearch(string fragment)
    {
        using var temp = new Store();
        Assert.Throws<ArgumentException>(() => temp.Catalog.Search("", filters: new SearchFilters(Sender: fragment)));
    }

    [Fact]
    public void SenderIndexTracksReplacementAndDeletionAndHonoursOtherCriteria()
    {
        using var temp = new Store();
        temp.Catalog.ReplaceArchive([Document("old", "old@harbourtonfielding.invalid", "Old")], "one");
        Assert.Single(temp.Catalog.Search("", filters: new SearchFilters(Sender: "fielding")).Results);
        temp.Catalog.ReplaceArchive([
            Document("new", "new@harbourtonfielding.invalid", "New"),
            Document("wrong-folder", "else@harbourtonfielding.invalid", "Else") with { Folder = "Elsewhere" }
        ], "two");
        var filters = new SearchFilters(Sender: "fielding", Subject: "new");
        Assert.Equal("new", Assert.Single(temp.Catalog.Search("", folder: "Inbox", filters: filters).Results).Subject);
        Assert.Empty(temp.Catalog.Search("", source: "local", filters: filters).Results);
        Assert.Empty(temp.Catalog.Search("", filters: new SearchFilters(Sender: "old@")).Results);
        temp.Catalog.ReplaceArchive([], "three");
        Assert.Empty(temp.Catalog.Search("", filters: filters).Results);
    }

    [Fact]
    public async Task CurrentSenderIndexCanBeSearchedWhileAnotherWriterIsPending()
    {
        using var temp = new Store();
        temp.Catalog.ReplaceArchive([Document("wanted", "a@harbourtonfielding.invalid", "Name")], "one");
        var filters = new SearchFilters(Sender: "fielding");
        Assert.Single(temp.Catalog.Search("", filters: filters).Results);
        using var connection = new SqliteConnection($"Data Source={temp.Paths.SearchCatalogPath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE documents SET is_read=1";
        command.ExecuteNonQuery();
        var read = Task.Run(() => temp.Catalog.Search("", filters: filters));
        try { Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2)))); }
        finally { transaction.Rollback(); }
        Assert.Single((await read).Results);
    }

    [Fact]
    public void PortableArchiveFromUsesHeadersWithoutChangingTheFile()
    {
        using var temp = new Store();
        var location = Path.Combine(temp.Paths.Root, "portable");
        var file = Path.Combine(location, FolderBundle.FileName);
        using (var bundle = FolderBundle.Create(file, "Fixture", "local"))
            bundle.Add(System.Text.Encoding.UTF8.GetBytes("From: Name Only <helpdesk@harbourtonfielding.invalid>\r\nSubject: retained\r\n\r\nfixture"),
                true, "fixture:archive", "2026-01-01T00:00:00Z", 1);
        ArchiveManifest.Register(location, file);
        var before = File.ReadAllBytes(file);
        var archives = new ArchiveStore(temp.Paths);
        archives.Add(location);
        Assert.Single(archives.Search("", 500, "fielding").Hits);
        Assert.Single(archives.Search("", 500, "Name Only").Hits);
        Assert.Empty(archives.Search("", 500, "%invalid").Hits);
        Assert.Throws<ArgumentException>(() => archives.Search("", 500, "ha"));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public void ExistingHeaderCatalogBackfillsOnceAndUsesTheFragmentIndex()
    {
        using var temp = new Store();
        temp.Catalog.Initialize();
        using var connection = new SqliteConnection($"Data Source={temp.Paths.SearchCatalogPath};Pooling=False");
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<50000)
            INSERT INTO documents(message_ref,source,account,folder,source_key,fingerprint,subject,sender,sender_name)
            SELECT 'ref'||x,'archive','fixture','Inbox','key'||x,'fp'||x,'Fixture '||x,
                CASE WHEN x=37612 THEN 'a@harbourtonfielding.invalid' ELSE 'sender'||x||'@example.invalid' END,
                'Fixture '||x FROM n;
            """;
        insert.ExecuteNonQuery();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Single(temp.Catalog.Search("", filters: new SearchFilters(Sender: "fielding")).Results);
        var first = watch.ElapsedMilliseconds;
        watch.Restart();
        for (var n = 0; n < 10; n++)
            Assert.Single(temp.Catalog.Search("", filters: new SearchFilters(Sender: "fielding")).Results);
        Console.WriteLine($"sender-index headers=50000 first-search-including-index={first}ms repeat-average={watch.ElapsedMilliseconds / 10.0:F1}ms");
        using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN SELECT rowid FROM sender_fragments WHERE sender_fragments MATCH 'fielding'";
        using var reader = explain.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Contains("VIRTUAL TABLE INDEX", reader.GetString(3));
    }

    private static CatalogDocument Document(string key, string sender, string name) => new(
        MailContract.ArchiveSource, "fixture", "Inbox", key, null, key, sender, name,
        ["recipient@example.invalid"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 100, true, 0, "fixture");

    private sealed class Store : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-sender-tests", Guid.NewGuid().ToString("N"));
        public AppPaths Paths { get; }
        public SearchCatalog Catalog { get; }
        public Store()
        {
            Directory.CreateDirectory(_root);
            var services = new MailkeepServices(_root);
            Paths = services.Paths;
            Catalog = services.Catalog;
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
