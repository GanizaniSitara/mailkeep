using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

internal static class BulkWorkflowSmoke
{
    public static int Run(string app, string shots, bool consolidatePilots = false)
    {
        Check(Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app), "Insiders VM fixture required");
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-bulk-workflow-" + Guid.NewGuid().ToString("N"));
        var services = new MailkeepServices(root); var db = services.Database;
        db.Initialize(); db.SetSetting("sync.auto", "off");
        if (consolidatePilots)
        {
            services.Accounts.Save([new MailAccount("local:second", "custom", "second@example.invalid", "INBOX", "Inbox",
                "imap.example.invalid", null, null, new KeepAliveSettings(), MailboxModes.LocalKeep)]);
        }
        var parent = db.CreateLocalFolder("Parent @", null, "archive", "OE|Parent @");
        var imported = db.CreateLocalFolder("Child", parent.Key, "archive", "OE|Parent @/Child");
        db.CreateLocalFolder("Empty child", parent.Key, "archive", "OE|Parent @/Empty child");
        var native = db.CreateLocalFolder("Native", null);
        var emptyWritable = consolidatePilots ? db.CreateLocalFolder("Empty writable", null) : null;
        var ids = new List<long>();
        var expectedMime = new Dictionary<string, byte[]>();
        for (var i = 0; i < 30; i++)
        {
            var raw = Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: Bulk {i}\r\n\r\nbulkneedle {i}\r\n");
            expectedMime.Add("Bulk " + i, raw);
            ids.Add(db.Upsert(new(i < 20 ? "archive" : "live", i < 20 ? "OE" : consolidatePilots && i % 2 != 0 ? "local:second" : "__mailkeep_local_copy__",
                i < 20 ? "Parent @/Child" : native.Key, "bulk:" + i, null, 0, "Bulk " + i,
                "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddMinutes(i), raw.Length,
                i % 2 == 0, 0, "bulkneedle " + i, null, services.Blobs.Put(raw))));
        }
        services.Catalog.SyncOperational(); services.Catalog.SyncArchive();
        var originalReference = services.Catalog.Search("bulkneedle", "archive", 100).Results.Single(m => m.Id == ids[0]).MessageRef!;
        Directory.CreateDirectory(shots);
        var moved = false;
        Ui("before");
        if (consolidatePilots) Mailkeep.TestFixtures.PortableStorageFixture.SeedLegacy(services);
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        if (consolidatePilots)
        {
            Check(migration.ConsolidatePilots(migration.PreviewPilots().Confirmation).Phase == "complete", "Pilot consolidation failed");
            Check(db.FolderStorage.Enabled && db.FolderStorage.Inspect().Enabled, "Unified status or writer state differs");
        }
        Ui("converted"); Ui("restarted");
        Check(migration.RebuildProjection() == 30, "Rebuild count differs");
        Ui("rebuilt");
        Check(db.ListMessages().Select(m => m.Id).Order().SequenceEqual(ids.Order()), "IDs changed");
        Check(services.Catalog.Search("bulkneedle", "all", 100).Count == 30, "Search changed");
        Check(db.AdoptMessages([ids[0]], native.Key) == 1, "Converted import move failed");
        moved = true;
        services.Catalog.SyncOperational();
        Check(db.ListMessages().Count == 30 && db.GetMessage(ids[0])!.Summary.Folder == native.Key,
            "Move created a hidden original or changed identity");
        Ui("moved"); Ui("moved-restarted");
        Check(migration.RebuildProjection() == 30, "Move recovery created an extra occurrence");
        Ui("moved-rebuilt");
        var archiveLocation = Path.Combine(root, "detached"); Directory.CreateDirectory(archiveLocation);
        Check(new FolderArchive(services.Paths).Move(native.Key, archiveLocation, true, backup: false).Removed == 11,
            "Archiving moved messages lost occurrences");
        var restored = new ArchiveMoveBack(services).Run(archiveLocation, "Native", execute: true);
        var restoredId = db.ListMessages().Single(m => m.Folder == restored.FolderKeys.Single() && m.Subject == "Bulk 0").Id;
        Check(services.Catalog.Resolve(originalReference)?.LocalId == restoredId,
            "Original imported reference failed after archive/restore");
        Check(db.ListMessages().Count == 30, "Archive/restore duplicated messages");
        db.FolderStorage.Flush();
        // Export only disposable fixture databases for a separate Python/SQLite byte audit.
        string Snapshot(string file)
        {
            var proof = Path.Combine(root, "proof-" + Guid.NewGuid().ToString("N") + ".db");
            using (var source = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False"))
            using (var target = new SqliteConnection($"Data Source={proof};Pooling=False"))
            { source.Open(); target.Open(); source.BackupDatabase(target); }
            return Convert.ToBase64String(File.ReadAllBytes(proof));
        }
        File.WriteAllText(Path.Combine(shots, consolidatePilots ? "pilot-workflow-audit.json" : "bulk-workflow-audit.json"),
            JsonSerializer.Serialize(new
            {
                expected = expectedMime, projection = Snapshot(services.Paths.DatabasePath),
                portable = Directory.GetFiles(Path.Combine(root, "bound-folders"), FolderBundle.FileName, SearchOption.AllDirectories).Select(Snapshot).ToArray(),
                legacy = consolidatePilots ? Directory.GetFiles(Path.Combine(root, "local-folders"), FolderBundle.FileName, SearchOption.AllDirectories).Select(Snapshot).ToArray() : [],
                originalReference, restoredId
            }));
        File.WriteAllText(Path.Combine(shots, "bulk-workflow.json"), JsonSerializer.Serialize(new
        { pass = true, root, messages = 30, pilotConsolidated = consolidatePilots, checks = "before/after/restart/rebuild UI; move preserves ID with no hidden original; moved restart/rebuild; nested and empty tree nodes; All Local Mail root empty; Find 30" }));
        return 0;

        void Ui(string stage)
        {
            // Prove that converted mail opens without the legacy raw cache, on every restart/rebuild.
            // root is the private GUID fixture allocated above, never the owner mailbox.
            if (stage != "before" && Directory.Exists(services.Paths.BlobDirectory))
                Directory.Delete(services.Paths.BlobDirectory, recursive: true);
            void Trace(string step) => File.AppendAllText(Path.Combine(shots, "bulk-workflow-trace.json"), JsonSerializer.Serialize(new { stage, step, utc = DateTimeOffset.UtcNow }) + "\n");
            Trace("begin");
            Exception? error = null;
            var thread = new Thread(() => { try { Exercise(); } catch (Exception e) { error = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(90)), "UI timeout: " + stage);
            if (error is not null) throw new InvalidOperationException(stage, error);
            void Exercise()
            {
                Forms.Application.EnableVisualStyles(); Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var type = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Trace("constructing");
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                Trace("constructed");
                T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(main)!;
                void Wait(Func<bool> ready)
                {
                    var watch = Stopwatch.StartNew();
                    while (!ready()) { Check(watch.Elapsed.TotalSeconds < 25, "UI did not settle: " + stage); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
                main.Show(); Wait(() => Field<bool>("_ready"));
                Trace("ready");
                var tree = Field<Forms.TreeView>("_localTree");
                foreach (var (folder, expectedIds) in new[] { (imported, ids.Skip(moved ? 1 : 0).Take(moved ? 19 : 20).ToArray()),
                    (native, (moved ? ids.Skip(20).Append(ids[0]) : ids.Skip(20)).ToArray()) })
                {
                    var count = expectedIds.Length;
                    var node = tree.Nodes.Find("LocalFolder:" + folder.Key, true).Single();
                    Check(node.Parent!.Name == (folder.ParentKey is null ? "AllLocalMail" : "LocalFolder:" + folder.ParentKey), "Hierarchy changed");
                    tree.SelectedNode = node; Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == count);
                    var actual = Field<IReadOnlyList<object>>("_rows").OfType<MessageSummary>().Select(m => m.Id).Order();
                    Check(actual.SequenceEqual(expectedIds.Order()), "Visible IDs differ");
                }
                Check(tree.Nodes.Find("LocalFolder:" + parent.Key, true).Single().Nodes.Count == 2, "Empty child lost");
                if (emptyWritable is not null)
                {
                    var emptyNode = tree.Nodes.Find("LocalFolder:" + emptyWritable.Key, true).Single();
                    tree.SelectedNode = emptyNode;
                    Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == 0);
                }
                tree.SelectedNode = tree.Nodes.Find("AllLocalMail", true).Single();
                Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == 0);
                tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + imported.Key, true).Single();
                Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == (moved ? 19 : 20));
                main.Refresh(); Forms.Application.DoEvents();
                using (var image = new System.Drawing.Bitmap(main.Width, main.Height))
                { main.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, main.Width, main.Height)); image.Save(Path.Combine(shots, "bulk-workflow-" + stage + ".png")); }
                using var find = (Forms.Form)type.GetMethod("FindForm", flags)!.Invoke(main, null)!; find.Show();
                Trace("find");
                All(find).OfType<Forms.TextBox>().Single(c => c.AccessibleName == "Message criteria").Text = "bulkneedle";
                All(find).OfType<Forms.Button>().Single(c => c.AccessibleName == "Find Now").PerformClick();
                Wait(() => All(find).OfType<Forms.Label>().Single(c => c.AccessibleName == "Find status").Text.StartsWith("30 messages found"));
                find.Close(); main.Close();
                Trace("closed");
            }
        }
    }

    private static IEnumerable<Forms.Control> All(Forms.Control root) => root.Controls.Cast<Forms.Control>().SelectMany(c => new[] { c }.Concat(All(c)));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
