using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailkeep.Tests;

public sealed class AttachmentSearchTests
{
    private static readonly object ConsoleLock = new();

    [Fact]
    public void PortableAttachmentIndexAndExtractionDoNotRequireOrRepopulateRawCache()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        var payload = Encoding.UTF8.GetBytes("orchid telescope portable attachment");
        var fixture = AddMessage(services, "portable", "evidence.txt", "text/plain", payload);
        var file = services.Blobs.FullPath(fixture.RawPath);
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        File.Delete(file);
        services.Catalog.SyncOperational();
        Assert.Equal(1, services.Catalog.SyncAttachments());
        var hit = Assert.Single(services.Catalog.Search("orchid telescope",
            filters: new SearchFilters(MatchScope: SearchMatchScope.AttachmentContent)).Results);
        var extracted = services.Catalog.OpenAttachment(hit.AttachmentMatch!.OccurrenceId);
        Assert.Equal(payload, File.ReadAllBytes(extracted));
        Assert.False(File.Exists(file));
        var messageId = Assert.Single(services.Database.ListMessages()).Id;
        Assert.Single(services.Database.GetMessage(messageId)!.Attachments);
        File.Delete(file);
        Assert.Equal(payload, File.ReadAllBytes(services.Database.ExtractAttachment(messageId, 0)));
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(SearchMatchScope.Message, "ordinary message")]
    [InlineData(SearchMatchScope.AttachmentName, "evidence")]
    [InlineData(SearchMatchScope.AttachmentContent, "orchid telescope")]
    public void HeaderCriteriaConstrainTheParentAcrossAllTextScopes(SearchMatchScope scope, string query)
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "wanted", "evidence.txt", "text/plain", Encoding.UTF8.GetBytes("orchid telescope"));
        AddMessage(services, "other", "evidence.txt", "text/plain", Encoding.UTF8.GetBytes("orchid telescope"));
        services.Catalog.SyncOperational();
        services.Catalog.SyncAttachments();
        var filters = new SearchFilters(MatchScope: scope, Sender: "sender@example.invalid",
            Recipient: "recipient@example.invalid", Subject: "Attachment fixture wanted");
        Assert.Equal("Attachment fixture wanted", Assert.Single(services.Catalog.Search(query, filters: filters).Results).Subject);
        Assert.Empty(services.Catalog.Search(query, filters: filters with { Sender = "absent@example.invalid" }).Results);
        Assert.Empty(services.Catalog.Search(query, filters: filters with { Recipient = "absent@example.invalid" }).Results);
        Assert.Empty(services.Catalog.Search(query, filters: filters with { Subject = "absent subject" }).Results);
        Assert.Equal("Attachment fixture wanted", Assert.Single(services.Catalog.Search("", filters: filters).Results).Subject);
    }

    [Fact]
    public void CatalogIndexesLegacyRawPathLookupByLocalId()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        services.Catalog.Initialize();

        using var connection = new SqliteConnection($"Data Source={services.Paths.SearchCatalogPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_index_list('documents') WHERE name='idx_catalog_local_id'";

        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void PhraseOnlyInsideTextAttachmentIsSearchableAndScoped()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "text", "evidence.txt", "text/plain", Encoding.UTF8.GetBytes("orchid telescope phrase"));

        services.Catalog.SyncOperational(force: true);
        Assert.Equal(1, services.Catalog.SyncAttachments());

        var all = services.Catalog.Search("orchid telescope");
        var hit = Assert.Single(all.Results);
        Assert.Equal("evidence.txt", hit.AttachmentMatch?.FileName);
        Assert.Equal("content", hit.AttachmentMatch?.MatchKind);
        Assert.Contains("orchid", hit.AttachmentMatch?.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(services.Catalog.Search("orchid telescope", filters:
            new SearchFilters(MatchScope: SearchMatchScope.Message)).Results);
        Assert.Single(services.Catalog.Search("orchid telescope", filters:
            new SearchFilters(MatchScope: SearchMatchScope.AttachmentContent)).Results);
        Assert.Equal(0, services.Catalog.SyncAttachments());
    }

    [Fact]
    public void FilenameSearchWorksWhenContentIsUnsupported()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "binary", "project-nebula.custom", "application/octet-stream", [0, 1, 2, 3, 4]);
        services.Catalog.SyncOperational(force: true);
        services.Catalog.SyncAttachments();

        var hit = Assert.Single(services.Catalog.Search("project-nebula", filters:
            new SearchFilters(MatchScope: SearchMatchScope.AttachmentName)).Results);
        Assert.Equal("filename", hit.AttachmentMatch?.MatchKind);
        Assert.Equal(1, services.Catalog.AttachmentStatus().PayloadsSkipped);
    }

    [Fact]
    public void HtmlOfficeAndTextLayerPdfAreExtractedWithoutActiveOrMetadataContent()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "html", "page.html", "text/html", Encoding.UTF8.GetBytes(
            "<style>.secret{}</style><script>forbiddenScriptToken</script><p>visible marigold wording</p>"));
        AddMessage(services, "docx", "brief.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Ooxml("word/document.xml", "<w:document xmlns:w=\"urn:w\"><w:body><w:p><w:r><w:t>cobalt meadow wording</w:t></w:r></w:p></w:body></w:document>"));
        AddMessage(services, "pdf", "brief.pdf", "application/pdf", Pdf("silver compass wording"));
        services.Catalog.SyncOperational(force: true);
        Assert.Equal(3, services.Catalog.SyncAttachments());

        Assert.Single(services.Catalog.Search("visible marigold").Results);
        Assert.Empty(services.Catalog.Search("forbiddenScriptToken").Results);
        Assert.Single(services.Catalog.Search("cobalt meadow").Results);
        Assert.Single(services.Catalog.Search("silver compass").Results);
    }

    [Fact]
    public void CsvJsonXmlNestedEmlSpreadsheetAndPresentationAreSearchable()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "csv", "table.csv", "text/csv", Encoding.UTF8.GetBytes("name,value\nindigo,harbour phrase"));
        AddMessage(services, "json", "record.json", "application/json", Encoding.UTF8.GetBytes("{\"note\":\"amber orchard phrase\"}"));
        AddMessage(services, "xml", "record.xml", "application/xml", Encoding.UTF8.GetBytes("<note>violet bridge phrase</note>"));
        AddMessage(services, "eml", "forwarded.eml", "message/rfc822", Eml("crimson lantern phrase"));
        AddMessage(services, "xlsx", "sheet.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Ooxml("xl/sharedStrings.xml", "<sst xmlns=\"urn:x\"><si><t>turquoise valley phrase</t></si></sst>"));
        AddMessage(services, "pptx", "slides.pptx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            Ooxml("ppt/slides/slide1.xml", "<p:sld xmlns:p=\"urn:p\"><p:t>scarlet mountain phrase</p:t></p:sld>"));
        services.Catalog.SyncOperational(force: true);
        Assert.Equal(6, services.Catalog.SyncAttachments());

        foreach (var phrase in new[] { "indigo harbour", "amber orchard", "violet bridge", "crimson lantern",
                     "turquoise valley", "scarlet mountain" })
            Assert.Single(services.Catalog.Search(phrase).Results);
    }

    [Fact]
    public void IdenticalPayloadsExtractOnceAndEveryOccurrenceCanResolve()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        var payload = Encoding.UTF8.GetBytes("shared saffron payload");
        AddMessage(services, "one", "first.txt", "text/plain", payload);
        AddMessage(services, "two", "second.txt", "text/plain", payload);
        services.Catalog.SyncOperational(force: true);
        services.Catalog.SyncAttachments();

        var results = services.Catalog.Search("shared saffron");
        Assert.Equal(2, results.Count);
        Assert.Equal(2, results.Results.Count);
        Assert.Equal(1, services.Catalog.AttachmentStatus().PayloadsIndexed);
        var match = Assert.IsType<AttachmentMatch>(results.Results[0].AttachmentMatch);
        var preview = services.Catalog.OpenAttachment(match.OccurrenceId);
        Assert.Equal(payload, File.ReadAllBytes(preview));
        if (OperatingSystem.IsWindows())
            Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(preview + ":Zone.Identifier"));
    }

    [Fact]
    public void ImageAndArchiveStatusesExplainWhyContentWasNotIndexed()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "image", "scan.png", "image/png", [137, 80, 78, 71]);
        AddMessage(services, "archive", "bundle.zip", "application/zip", [80, 75, 3, 4]);
        services.Catalog.SyncOperational(force: true);
        services.Catalog.SyncAttachments();

        var status = services.Catalog.AttachmentStatus();
        Assert.Equal(1, status.PayloadsNeedsOcr);
        Assert.Equal(1, status.PayloadsSkipped);
        Assert.Single(services.Catalog.Search("scan.png", filters:
            new SearchFilters(MatchScope: SearchMatchScope.AttachmentName)).Results);
    }

    [Fact]
    public void RasterAttachmentUsesOcrExtractorAndRemainsRetryableWhenNoTextIsRecovered()
    {
        var extraction = AttachmentTextExtraction.Extract([137, 80, 78, 71], "scan.png", "image/png");

        Assert.Equal("ocr", extraction.ExtractorId);
        Assert.Equal("needs_ocr", extraction.Status);
        Assert.StartsWith("ocr_", extraction.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentWorkerAcceptsInlinePayloadWithoutAStagingPath()
    {
        var request = new AttachmentWorkerRequest(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("inline worker text")), "inline.txt", "text/plain");
        var input = new StringReader(JsonSerializer.Serialize(request) + Environment.NewLine);
        var output = new StringWriter();
        var originalInput = Console.In;
        var originalOutput = Console.Out;

        lock (ConsoleLock)
        {
            Console.SetIn(input);
            Console.SetOut(output);
            try
            {
                AttachmentWorkerProtocol.RunAsync().GetAwaiter().GetResult();
            }
            finally
            {
                Console.SetIn(originalInput);
                Console.SetOut(originalOutput);
            }
        }

        var extraction = JsonSerializer.Deserialize<AttachmentExtraction>(output.ToString());
        Assert.NotNull(extraction);
        Assert.Equal("indexed", extraction!.Status);
        Assert.Equal("inline worker text", extraction.Text);
        Assert.DoesNotContain("Path", JsonSerializer.Serialize(request));
    }

    [Fact]
    public void EstimatorUsesAnIsolatedFtsAndDoesNotMarkPayloadsIndexed()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "estimate-one", "one.txt", "text/plain", Encoding.UTF8.GetBytes("estimated bluebell phrase"));
        AddMessage(services, "estimate-two", "two.txt", "text/plain", Encoding.UTF8.GetBytes("estimated foxglove phrase"));
        services.Catalog.SyncOperational(force: true);

        var estimate = services.Catalog.EstimateAttachments(1);

        Assert.Equal(2, estimate.CorpusRawBlobs);
        Assert.Equal(1, estimate.SampleRawBlobs);
        Assert.True(estimate.ProjectedFtsBytes > 0);
        var status = services.Catalog.AttachmentStatus();
        Assert.Equal(2, status.RawBlobsPending);
        Assert.Equal(0, status.PayloadsIndexed);
    }

    [Fact]
    public void FilenamesRemainSearchableForSupportedUnsupportedTooLargeAndFailedPayloads()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "supported-name", "supported-cirrus.txt", "text/plain", Encoding.UTF8.GetBytes("plain content"));
        AddMessage(services, "unsupported-name", "unsupported-cirrus.bin", "application/octet-stream", [0, 1, 2]);
        AddMessage(services, "too-large-name", "oversize-cirrus.txt", "text/plain",
            new byte[AttachmentTextExtraction.MaxInputBytes + 1]);
        AddMessage(services, "failed-name", "broken-cirrus.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [80, 75, 3, 4, 1]);
        services.Catalog.SyncOperational(force: true);
        Assert.Equal(4, services.Catalog.SyncAttachments());

        foreach (var name in new[] { "supported-cirrus", "unsupported-cirrus", "oversize-cirrus", "broken-cirrus" })
            Assert.Single(services.Catalog.Search(name, filters:
                new SearchFilters(MatchScope: SearchMatchScope.AttachmentName)).Results);
        Assert.True(services.Catalog.AttachmentStatus().FilenameSearchableContentUnavailable >= 3);
    }

    [Fact]
    public void OneRawBlobAcrossLocationsIsDiscoveredOnceAndMerged()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        var original = AddMessage(services, "shared-location", "location.txt", "text/plain",
            Encoding.UTF8.GetBytes("location heliotrope phrase"));
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "primary", "Secondary", "shared-location-copy",
            null, 1, original.Subject, "sender@example.invalid", "Sender", ["recipient@example.invalid"],
            original.Date, original.Size, false, 1, "ordinary message body", null, original.RawPath));
        services.Catalog.SyncOperational(force: true);

        Assert.Equal(1, services.Catalog.SyncAttachments());
        var hit = Assert.Single(services.Catalog.Search("location heliotrope").Results);
        Assert.Equal(2, hit.Locations?.Count);
        Assert.Equal(1, services.Catalog.AttachmentStatus().RawBlobsIndexed);
    }

    [Fact]
    public void SeveralMatchingAttachmentsReturnOneLogicalMessageAndBestMatchCount()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessageWithAttachments(services, "several", [
            new("one.txt", "text/plain", Encoding.UTF8.GetBytes("juniper signal first")),
            new("two.txt", "text/plain", Encoding.UTF8.GetBytes("juniper signal second"))
        ]);
        services.Catalog.SyncOperational(force: true);
        services.Catalog.SyncAttachments();

        var hit = Assert.Single(services.Catalog.Search("juniper signal", filters:
            new SearchFilters(MatchScope: SearchMatchScope.AttachmentContent)).Results);
        Assert.Equal(2, hit.AttachmentMatch?.MatchingAttachmentCount);
        Assert.Contains("juniper", hit.AttachmentMatch?.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancellationCommitsCompletedBlobsAndRestartDoesNotReextractThem()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        for (var index = 0; index < 30; index++)
            AddMessage(services, $"cancel-{index}", $"cancel-{index}.txt", "text/plain",
                Encoding.UTF8.GetBytes($"resume payload {index}"));
        services.Catalog.SyncOperational(force: true);
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => services.Catalog.SyncAttachments(progress: step =>
        {
            if (step.Done == 25) cancellation.Cancel();
        }, cancellationToken: cancellation.Token));
        Assert.Equal("cancelled", services.Catalog.AttachmentStatus().LastRunStatus);
        Assert.Equal(25, services.Catalog.AttachmentStatus().PayloadsIndexed);

        Assert.Equal(5, services.Catalog.SyncAttachments());
        Assert.Equal(0, services.Catalog.SyncAttachments());
        Assert.Equal(30, services.Catalog.AttachmentStatus().PayloadsIndexed);
        Assert.Equal("complete", services.Catalog.AttachmentStatus().LastRunStatus);
    }

    [Fact]
    public void ExtractorRebuildReextractsItsPayloadWhileMimeRediscoveryReusesIt()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "versions", "versions.txt", "text/plain", Encoding.UTF8.GetBytes("versioned payload"));
        services.Catalog.SyncOperational(force: true);
        services.Catalog.SyncAttachments();
        Assert.Equal(1, services.Catalog.RebuildAttachmentExtractor("text", execute: false));
        Assert.Equal(1, services.Catalog.RebuildAttachmentExtractor("text", execute: true));
        Assert.Equal(1, services.Catalog.SyncAttachments());

        var attemptsBefore = ScalarLong(services.Paths.SearchCatalogPath,
            "SELECT attempt_count FROM attachments WHERE extractor_id='text'");
        Execute(services.Paths.SearchCatalogPath, "UPDATE raw_blob_scans SET mime_scan_version=0");
        Assert.Equal(1, services.Catalog.SyncAttachments());
        Assert.Equal(attemptsBefore, ScalarLong(services.Paths.SearchCatalogPath,
            "SELECT attempt_count FROM attachments WHERE extractor_id='text'"));
        Assert.Equal(0, services.Catalog.SyncAttachments());
    }

    [Fact]
    public void SharedPayloadBecomesGcEligibleOnlyAfterItsLastDocumentReferenceIsRemoved()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        var payload = Encoding.UTF8.GetBytes("shared gc payload");
        var first = AddMessage(services, "gc-one", "gc-one.txt", "text/plain", payload);
        var second = AddMessage(services, "gc-two", "gc-two.txt", "text/plain", payload);
        services.Catalog.SyncOperational(force: true);
        services.Catalog.SyncAttachments();

        DeleteDocumentsForRaw(services.Paths.SearchCatalogPath, RawHash(first.RawPath));
        var shared = services.Catalog.AttachmentStatus();
        Assert.Equal(1, shared.UnreferencedOccurrences);
        Assert.Equal(0, shared.UnreferencedPayloads);
        Assert.Single(services.Catalog.Search("shared gc").Results);

        DeleteDocumentsForRaw(services.Paths.SearchCatalogPath, RawHash(second.RawPath));
        var orphaned = services.Catalog.AttachmentStatus();
        Assert.Equal(2, orphaned.UnreferencedOccurrences);
        Assert.Equal(1, orphaned.UnreferencedPayloads);
    }

    [Fact]
    public void AttachmentIndexingDoesNotModifySourceDatabaseOrRawMessage()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        var fixture = AddMessage(services, "immutable", "immutable.txt", "text/plain",
            Encoding.UTF8.GetBytes("immutable source payload"));
        services.Catalog.SyncOperational(force: true);
        var rawPath = Path.Combine(services.Paths.BlobDirectory, fixture.RawPath);
        var rawHash = SHA256.HashData(File.ReadAllBytes(rawPath));
        var rawTime = File.GetLastWriteTimeUtc(rawPath);
        var databaseHash = SHA256.HashData(File.ReadAllBytes(services.Paths.DatabasePath));
        var databaseTime = File.GetLastWriteTimeUtc(services.Paths.DatabasePath);

        services.Catalog.SyncAttachments();

        Assert.Equal(rawHash, SHA256.HashData(File.ReadAllBytes(rawPath)));
        Assert.Equal(rawTime, File.GetLastWriteTimeUtc(rawPath));
        Assert.Equal(databaseHash, SHA256.HashData(File.ReadAllBytes(services.Paths.DatabasePath)));
        Assert.Equal(databaseTime, File.GetLastWriteTimeUtc(services.Paths.DatabasePath));
    }

    [Fact]
    public void MalformedMimeAndRetryableWorkerFailureDoNotBlockFollowingPayloads()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        var badRaw = services.Blobs.Put(Encoding.UTF8.GetBytes("not an RFC822 message\0\0"));
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "primary", "Primary", "bad-mime", null, 1,
            "Malformed MIME", "sender@example.invalid", "Sender", ["recipient@example.invalid"],
            DateTimeOffset.Parse("2026-08-20T08:00:00Z"), 22, false, 1, "ordinary message body", null, badRaw));
        AddMessage(services, "after-failure", "after.txt", "text/plain", Encoding.UTF8.GetBytes("following payload survives"));
        services.Catalog.SyncOperational(force: true);

        Assert.Equal(2, services.Catalog.SyncAttachments());
        Assert.Single(services.Catalog.Search("following payload").Results);
        Assert.Equal(1, services.Catalog.AttachmentStatus().RawBlobsFailed);
        Assert.Equal(1, services.Catalog.SyncAttachments());
        Assert.Equal(0, services.Catalog.AttachmentStatus().RawBlobsPending);

        Execute(services.Paths.SearchCatalogPath, """
            DELETE FROM attachment_text_fts WHERE rowid IN (SELECT id FROM attachments WHERE extractor_id='text');
            DELETE FROM attachment_text WHERE attachment_id IN (SELECT id FROM attachments WHERE extractor_id='text');
            UPDATE attachments SET status='failed',reason='worker_timeout',attempt_count=1 WHERE extractor_id='text';
            """);
        Assert.Equal(1, services.Catalog.SyncAttachments());
        Assert.Single(services.Catalog.Search("following payload").Results);
        Assert.Contains("indexed", services.Catalog.AttachmentStatus().Outcomes.Keys);
    }

    [Fact]
    public void OldMessageCatalogMigratesTwiceWithoutLosingSearch()
    {
        using var temp = new TempDirectory();
        var services = new MailkeepServices(temp.Path);
        services.Paths.EnsureDirectories();
        using (var connection = new SqliteConnection($"Data Source={services.Paths.SearchCatalogPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, message_ref TEXT NOT NULL UNIQUE,
                    source TEXT NOT NULL, account TEXT NOT NULL, folder TEXT NOT NULL, source_key TEXT NOT NULL,
                    local_id INTEGER, fingerprint TEXT NOT NULL, rfc_message_id TEXT, subject TEXT NOT NULL DEFAULT '',
                    sender TEXT NOT NULL DEFAULT '', sender_name TEXT NOT NULL DEFAULT '', recipients_json TEXT NOT NULL DEFAULT '[]',
                    date_utc TEXT, size INTEGER NOT NULL DEFAULT 0, is_read INTEGER NOT NULL DEFAULT 0,
                    attachment_count INTEGER NOT NULL DEFAULT 0, body_text TEXT NOT NULL DEFAULT '');
                CREATE VIRTUAL TABLE documents_fts USING fts5(subject,sender,recipients,body_text,
                    tokenize='unicode61 remove_diacritics 2');
                CREATE TABLE source_state(source TEXT PRIMARY KEY,generation TEXT NOT NULL,count INTEGER NOT NULL,indexed_at TEXT NOT NULL);
                INSERT INTO documents(message_ref,source,account,folder,source_key,fingerprint,subject,sender,body_text)
                    VALUES('msg_legacy','local','primary','Primary','legacy','legacy-fingerprint','Legacy migration','old@example.invalid','legacy migration phrase');
                INSERT INTO documents_fts(rowid,subject,sender,recipients,body_text)
                    VALUES(1,'Legacy migration','old@example.invalid','','legacy migration phrase');
                """;
            command.ExecuteNonQuery();
        }

        services.Catalog.Initialize();
        services.Catalog.Initialize();

        Assert.Single(services.Catalog.Search("legacy migration").Results);
        Assert.Equal(1, ScalarLong(services.Paths.SearchCatalogPath,
            "SELECT COUNT(*) FROM pragma_table_info('documents') WHERE name='raw_blob_hash'"));
        Assert.Equal(1, ScalarLong(services.Paths.SearchCatalogPath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='attachment_runs'"));
    }

    [Fact]
    public void TenThousandDuplicateDocumentOccurrencesAreBatchedAndNoChangeSyncIsEmpty()
    {
        using var temp = new TempDirectory();
        var services = Services(temp.Path);
        AddMessage(services, "performance", "performance.txt", "text/plain",
            Encoding.UTF8.GetBytes("ten thousand occurrence payload"));
        services.Catalog.SyncOperational(force: true);
        CloneCatalogDocument(services.Paths.SearchCatalogPath, 9_999);

        Assert.Equal(10_000, ScalarLong(services.Paths.SearchCatalogPath, "SELECT COUNT(*) FROM documents"));
        Assert.Equal(1, services.Catalog.SyncAttachments());
        Assert.Equal(0, services.Catalog.SyncAttachments());
        var stopwatch = Stopwatch.StartNew();
        var hit = Assert.Single(services.Catalog.Search("ten thousand occurrence", filters:
            new SearchFilters(MatchScope: SearchMatchScope.AttachmentContent)).Results);
        stopwatch.Stop();
        Assert.NotEmpty(hit.Locations ?? []);
        Console.WriteLine($"10,000 duplicate attachment occurrences searched in {stopwatch.ElapsedMilliseconds} ms");
    }

    private static MailkeepServices Services(string root)
    {
        var services = new MailkeepServices(root);
        services.Database.Initialize();
        return services;
    }

    private sealed record AddedMessage(string RawPath, long Size, string Subject, DateTimeOffset Date);
    private sealed record AttachmentFixture(string FileName, string MediaType, byte[] Payload);

    private static AddedMessage AddMessage(MailkeepServices services, string key, string fileName, string mediaType, byte[] payload)
        => AddMessageWithAttachments(services, key, [new(fileName, mediaType, payload)]);

    private static AddedMessage AddMessageWithAttachments(MailkeepServices services, string key,
        IReadOnlyList<AttachmentFixture> attachments)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.invalid"));
        message.To.Add(MailboxAddress.Parse("recipient@example.invalid"));
        message.Subject = "Attachment fixture " + key;
        message.MessageId = $"<{key}@attachments.invalid>";
        message.Date = DateTimeOffset.Parse("2026-08-20T08:00:00Z");
        var body = new Multipart("mixed") { new TextPart("plain") { Text = "ordinary message body" } };
        foreach (var fixture in attachments)
        {
            body.Add(new MimePart(ContentType.Parse(fixture.MediaType))
            {
                Content = new MimeContent(new MemoryStream(fixture.Payload, writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = fixture.FileName
            });
        }
        message.Body = body;
        using var raw = new MemoryStream();
        message.WriteTo(raw);
        var bytes = raw.ToArray();
        var rawPath = services.Blobs.Put(bytes);
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "primary", "Primary", key, null, 1,
            message.Subject, "sender@example.invalid", "Sender", ["recipient@example.invalid"], message.Date,
            bytes.Length, false, attachments.Count, "ordinary message body", null, rawPath));
        return new AddedMessage(rawPath, bytes.Length, message.Subject, message.Date);
    }

    private static long ScalarLong(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string RawHash(string rawPath) => Path.GetFileNameWithoutExtension(rawPath);

    private static void DeleteDocumentsForRaw(string databasePath, string rawHash)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var ids = connection.CreateCommand();
        ids.Transaction = transaction;
        ids.CommandText = "SELECT id FROM documents WHERE raw_blob_hash=$hash";
        ids.Parameters.AddWithValue("$hash", rawHash);
        var documentIds = new List<long>();
        using (var reader = ids.ExecuteReader())
            while (reader.Read()) documentIds.Add(reader.GetInt64(0));
        foreach (var id in documentIds)
        {
            using var fts = connection.CreateCommand();
            fts.Transaction = transaction;
            fts.CommandText = "DELETE FROM documents_fts WHERE rowid=$id";
            fts.Parameters.AddWithValue("$id", id);
            fts.ExecuteNonQuery();
        }
        using var documents = connection.CreateCommand();
        documents.Transaction = transaction;
        documents.CommandText = "DELETE FROM documents WHERE raw_blob_hash=$hash";
        documents.Parameters.AddWithValue("$hash", rawHash);
        documents.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void CloneCatalogDocument(string databasePath, int copies)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        for (var index = 0; index < copies; index++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO documents(message_ref,source,account,folder,source_key,local_id,fingerprint,rfc_message_id,
                                      subject,sender,sender_name,recipients_json,date_utc,size,is_read,attachment_count,
                                      body_text,raw_blob_hash,raw_path)
                SELECT $reference,source,account,folder,$source_key,NULL,fingerprint,rfc_message_id,
                       subject,sender,sender_name,recipients_json,date_utc,size,is_read,attachment_count,
                       body_text,raw_blob_hash,raw_path
                FROM documents ORDER BY id LIMIT 1
                """;
            command.Parameters.AddWithValue("$reference", $"msg_performance_{index}");
            command.Parameters.AddWithValue("$source_key", $"performance-copy-{index}");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static byte[] Ooxml(string entryName, string xml)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        return stream.ToArray();
    }

    private static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"""
        %PDF-1.4
        1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
        2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
        3 0 obj << /Type /Page /Parent 2 0 R /Contents 4 0 R >> endobj
        4 0 obj << /Length 64 >> stream
        BT /F1 12 Tf 72 720 Td ({text}) Tj ET
        endstream endobj
        trailer << /Root 1 0 R >>
        %%EOF
        """);

    private static byte[] Eml(string text)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("nested@example.invalid"));
        message.To.Add(MailboxAddress.Parse("reader@example.invalid"));
        message.Subject = "Nested message";
        message.Body = new TextPart("plain") { Text = text };
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailkeep-attachment-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
