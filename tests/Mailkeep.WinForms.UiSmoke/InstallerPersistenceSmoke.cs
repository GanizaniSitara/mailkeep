using System.Security.Cryptography;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using MimeKit;

/// <summary>Persistent invented mail, attachments, settings and account bytes for guest MSI lifecycle checks.</summary>
internal static class InstallerPersistenceSmoke
{
    internal static int Run(bool seed, string root)
    {
        if (Environment.GetEnvironmentVariable("MAILKEEP_MSI_FIXTURE") != "1"
            || !string.Equals(Path.GetFullPath(root), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mailkeep"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MSI fixture requires the explicitly owned guest default root.");
        var marker = Path.Combine(root, "installer-fixture.json");
        // Earlier disposable UI tests can leave global diagnostic logs only.
        // Preserve them; any existing mail, account, settings or other entry blocks seeding.
        if (seed && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root)
            .Any(path => Path.GetFileName(path) != "logs" || !Directory.Exists(path)))
            throw new InvalidOperationException("Refusing to seed an existing Mailkeep profile.");
        if (!seed && !File.Exists(marker)) throw new InvalidOperationException("Missing owned fixture marker.");
        var services = new MailkeepServices(root);
        services.Database.Initialize();
        if (seed)
        {
            services.Database.SetSetting("sync.auto", "off");
            services.Database.SetSetting("installer.fixture", "keep this preference");
            services.Accounts.Save([new MailAccount("msi-fixture", "custom", "fixture@example.invalid", "INBOX", "inbox",
                "example.invalid", null, "invented-fixture-password", new KeepAliveSettings(), MailboxModes.LocalKeep,
                new IncomingServerSettings(MailProtocols.Pop3, "example.invalid", 995))]);
            var proofs = new List<object>();
            foreach (var folder in new[] { LocalFolderKinds.Inbox, LocalFolderKinds.Sent, LocalFolderKinds.Deleted })
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("MSI fixture", "sender@example.invalid"));
                message.To.Add(MailboxAddress.Parse("fixture@example.invalid"));
                message.Subject = "Installer retained " + folder;
                message.Date = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
                var body = new BodyBuilder { TextBody = "Invented retained body " + folder, HtmlBody = "<p>Invented <b>retained</b> body " + folder + "</p>" };
                body.Attachments.Add("fixture.txt", System.Text.Encoding.UTF8.GetBytes("Retained attachment " + folder));
                message.Body = body.ToMessageBody();
                using var raw = new MemoryStream(); message.WriteTo(raw); var bytes = raw.ToArray(); raw.Position = 0;
                var id = services.Database.UpsertMimeMessageAsync(MailContract.LiveSource, "msi-fixture", folder,
                    "installer:" + folder, null, 0, message, raw, true).GetAwaiter().GetResult();
                proofs.Add(new { id, folder, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), subject = message.Subject });
            }
            var credentialFiles = Directory.GetFiles(root, "*.dpapi", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(root, path), Hash);
            File.WriteAllText(marker, JsonSerializer.Serialize(new { messages = proofs, credentialFiles }));
        }
        using var expected = JsonDocument.Parse(File.ReadAllText(marker));
        var count = 0;
        foreach (var proof in expected.RootElement.GetProperty("messages").EnumerateArray())
        {
            var content = services.Database.GetMessage(proof.GetProperty("id").GetInt64())
                ?? throw new InvalidOperationException("Installer lost a fixture message.");
            Check(content.Summary.Subject == proof.GetProperty("subject").GetString(), "Message subject changed");
            Check(content.Summary.Folder == proof.GetProperty("folder").GetString(), "Message folder changed");
            Check(content.BodyHtml?.Contains("<b>retained</b>") == true, "HTML changed");
            Check(content.Attachments.Count == 1 && content.Attachments[0].FileName == "fixture.txt", "Attachment lost");
            Check(content.RawPath is not null && Hash(content.RawPath) == proof.GetProperty("sha256").GetString(), "Raw MIME bytes changed");
            count++;
        }
        foreach (var file in expected.RootElement.GetProperty("credentialFiles").EnumerateObject())
            Check(Hash(Path.Combine(root, file.Name)) == file.Value.GetString(), "Encrypted account bytes changed");
        Check(services.Accounts.Load().Single().Name == "msi-fixture", "Account lost");
        Check(services.Database.GetSetting("installer.fixture") == "keep this preference", "Preference lost");
        Check(services.Database.GetSetting("sync.auto") == "off", "Background receive preference changed");
        Console.WriteLine($"PASS msi-{(seed ? "seed" : "check")} messages={count} raw-bytes=identical html=retained attachments=retained account=retained preferences=retained");
        return 0;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
