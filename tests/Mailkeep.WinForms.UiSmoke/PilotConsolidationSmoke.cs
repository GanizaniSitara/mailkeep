using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

// Storage-only disposable rehearsal. This scenario never constructs or launches a window.
internal static class PilotConsolidationSmoke
{
    // Read-only source snapshots supplied by the bounded rehearsal; never initialize a mailbox.
    public static int FolderCopy(string output)
    {
        output = Path.GetFullPath(output);
        var scratch = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
        if (scratch.Name != "scratch" || !output.StartsWith(scratch.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Folder rehearsal must stay inside this runner's scratch directory.");
        using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "input.json")));
        var key = input.RootElement.GetProperty("key").GetString()!;
        var hierarchy = input.RootElement.GetProperty("hierarchy").GetString()!;
        var metadata = input.RootElement.GetProperty("metadata").EnumerateObject()
            .ToDictionary(p => long.Parse(p.Name), p => p.Value.GetString()!);
        var watch = Stopwatch.StartNew();
        var target = Path.Combine(output, "upgraded.mkdb");
        FolderBundle.UpgradePilotCopy(Path.Combine(output, "original.mkdb"), target,
            new("live", "", key, hierarchy, true), metadata);
        using var upgraded = FolderBundle.Open(target);
        Check(upgraded.Verify().Ok && upgraded.List().Count == metadata.Count, "Upgraded folder verification failed");
        Console.WriteLine(JsonSerializer.Serialize(new { pass = true, messages = metadata.Count, seconds = watch.Elapsed.TotalSeconds }));
        return 0;
    }

    public static int ExistingCopy(string output, string confirmation)
    {
        output = Path.GetFullPath(output);
        var scratch = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
        if (scratch.Name != "scratch" || !output.StartsWith(scratch.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An existing-copy rehearsal must stay inside this runner's scratch directory.");
        var root = Path.Combine(output, "store");
        var migration = new BulkFolderMigration(new AppPaths(root));
        var plan = migration.PreviewPilots();
        var watch = Stopwatch.StartNew();
        var state = migration.ConsolidatePilots(confirmation, phase => Console.Error.WriteLine(phase));
        File.WriteAllText(Path.Combine(output, "converted.json"), JsonSerializer.Serialize(new
        { root, state.Phase, folders = plan.Folders.Length, messages = plan.Messages, seconds = watch.Elapsed.TotalSeconds }));
        return state.Phase == "complete" ? 0 : 1;
    }

    public static int Run(string output)
    {
        output = Path.GetFullPath(output);
        var root = Path.Combine(output, "store");
        if (Directory.Exists(root)) throw new IOException("Rehearsal destination already exists.");
        Directory.CreateDirectory(output);
        var services = new MailkeepServices(root); var db = services.Database; db.Initialize();
        services.Accounts.Save(new[] { "local:one", "local:two" }.Select(name => new MailAccount(name, "custom", name.Replace("local:", "") + "@example.invalid", "INBOX", "Inbox",
            "imap.example.invalid", null, null, new KeepAliveSettings(), MailboxModes.LocalKeep)).ToArray());
        db.SetSetting("sync.auto", "off");
        var folders = new List<string>();
        for (var n = 0; n < 39; n++)
        {
            var folder = db.CreateLocalFolder("Pilot " + n, n == 0 ? null : folders[0]);
            folders.Add(folder.Key);
            if (n == 38) continue; // An empty folder must survive publication and rebuild.
            for (var i = 0; i < 4; i++)
            {
                var raw = Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: Pilot {n}/{i}\r\n\r\npilotneedle {n}/{i} {new string('x', 4096)}\r\n");
                db.Upsert(new("live", i % 2 == 0 ? "local:one" : "local:two", folder.Key, $"pilot:{n}:{i}", null, 0,
                    $"Pilot {n}/{i}", "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddMinutes(n * 4 + i),
                    raw.Length, i % 2 == 0, 0, "pilotneedle", null, services.Blobs.Put(raw)));
            }
        }
        var allIds = db.ListMessages().Select(m => m.Id).Order().ToArray();
        var legacy = Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        db.MoveMessage(allIds[0], "inbox");
        Mailkeep.TestFixtures.PortableStorageFixture.RemoveLegacyOccurrence(legacy.Single(f => f.Key == folders[0]).File, allIds[0]); // Retain a tombstone in the old pilot.
        var bulk = new BulkFolderMigration(services.Paths);
        bulk.Run(bulk.Preview().Confirmation, publish: true); // Include the ordinary Inbox occurrence.
        services.Catalog.SyncOperational();
        var watch = Stopwatch.StartNew();
        var plan = bulk.PreviewPilots();
        var state = bulk.ConsolidatePilots(plan.Confirmation);
        File.WriteAllText(Path.Combine(output, "converted.json"), JsonSerializer.Serialize(new
        { root, state.Phase, folders = plan.Folders.Length, messages = plan.Messages, seconds = watch.Elapsed.TotalSeconds }));
        Check(state.Phase == "complete" && db.FolderStorage.Enabled, "Pilot writer remained active");
        Check(db.ListMessages().Select(m => m.Id).Order().SequenceEqual(allIds), "Occurrence identities changed");
        services.Catalog.SyncOperational();
        Check(services.Catalog.Search("pilotneedle", "local", 1000).Count == allIds.Length, "Search count changed");
        // Leave the just-converted store unchanged for the independent byte checker.
        File.WriteAllText(Path.Combine(output, "done.json"), JsonSerializer.Serialize(new
        { pass = true, root, folders = 39, messages = allIds.Length, check = "headless conversion; two accounts per folder; empty child; moved occurrence/tombstone; IDs/search preserved" }));
        Console.WriteLine(File.ReadAllText(Path.Combine(output, "done.json")));
        return 0;
    }

    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
}
