using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

internal static class AdoptionCleanupSmoke
{
    public static int Run(string app, string shots)
    {
        Check(Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app), "Insiders VM fixture required");
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-bulk-workflow-" + Guid.NewGuid().ToString("N"));
        var services = new MailkeepServices(root); var db = services.Database;
        db.Initialize(); db.SetSetting("sync.auto", "off");
        Directory.CreateDirectory(shots);
        var originals = new List<long>();
        for (var i = 0; i < 14; i++)
        {
            var raw = Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: Retained {i}\r\nContent-Type: text/html\r\n\r\n<p>Retained content {i}</p>\r\n");
            originals.Add(db.Upsert(new("archive", "OE Classic", i < 11 ? "November" : i < 13 ? "Friday" : "Sunday",
                "original:" + i, null, 0, "Retained " + i, "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddMinutes(i),
                raw.Length, i % 2 == 0, 0, "Retained content " + i, "<p>Retained content " + i + "</p>", services.Blobs.Put(raw))));
        }
        db.AdoptMessages(originals, "inbox");
        var copies = db.ListMessages(MailContract.LocalCopyAccount).Select(m => m.Id).Order().ToArray();
        Check(copies.Length == 14, "Fixture copy count differs");
        var before = copies.ToDictionary(id => id, id => File.ReadAllBytes(db.GetMessage(id)!.RawPath!));
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        Ui("before");
        // Opening the first message legitimately marks it read. Freeze the state
        // after that baseline UI interaction, before the cleanup under test.
        var readStates = copies.ToDictionary(id => id, id => db.GetMessage(id)!.Summary.IsRead);
        var report = new AdoptionMigrationReport(services.Paths).Run(default, null, originals);
        Check(new AdoptionCleanup(services.Paths).Apply(report).Removed == 14, "Cleanup count differs");
        Ui("cleaned"); Ui("restarted");
        File.Delete(services.Paths.DatabasePath);
        services = new MailkeepServices(root); db = services.Database; db.Initialize();
        Check(new BulkFolderMigration(services.Paths).RebuildProjection() == 14, "Fresh rebuild count differs");
        foreach (var link in report.Links)
            Check(db.GetMessage(link.Original!.Id)!.Summary.Id == link.Copies.Single().Id, "Old ID alias lost");
        foreach (var id in copies)
        {
            Check(before[id].SequenceEqual(File.ReadAllBytes(db.GetMessage(id)!.RawPath!)), "MIME changed");
            Check(db.GetMessage(id)!.Summary.IsRead == readStates[id] && db.GetMessage(id)!.Summary.Folder == "inbox", "Filed state changed");
        }
        Ui("rebuilt");
        string Snapshot(string file)
        {
            var target = Path.Combine(shots, Guid.NewGuid().ToString("N") + ".db");
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
            {
                source.Open(); destination.Open(); source.BackupDatabase(destination);
                using var mode = destination.CreateCommand(); mode.CommandText = "PRAGMA journal_mode=DELETE"; mode.ExecuteScalar();
            }
            var bytes = File.ReadAllBytes(target); File.Delete(target);
            return Convert.ToBase64String(bytes);
        }
        File.WriteAllText(Path.Combine(shots, "adoption-cleanup.json"), JsonSerializer.Serialize(new
        { pass = true, root, removed = 14, retained = copies,
            expected = report.Links.Select(link => new { original = link.Original!.Id, retained = link.Copies[0].Id,
                raw = Convert.ToBase64String(before[link.Copies[0].Id]), isRead = readStates[link.Copies[0].Id], folder = "inbox" }),
            projection = Snapshot(services.Paths.DatabasePath),
            portable = Directory.GetFiles(Path.Combine(root, "bound-folders"), "folder.mkdb", SearchOption.AllDirectories).Select(Snapshot).ToArray(),
            checks = "UI before/cleanup/restart/fresh rebuild; numeric aliases; exact bytes; folder and read state" }));
        return 0;

        void Ui(string stage)
        {
            Exception? error = null;
            var thread = new Thread(() => { try { Exercise(); } catch (Exception e) { error = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(60)), "UI timeout " + stage);
            if (error is not null) throw new InvalidOperationException(stage, error);
            void Exercise()
            {
                Forms.Application.EnableVisualStyles(); Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var type = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(main)!;
                void Wait(Func<bool> ready)
                {
                    var watch = Stopwatch.StartNew();
                    while (!ready()) { Check(watch.Elapsed.TotalSeconds < 25, "UI did not settle " + stage); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
                main.Show(); Wait(() => Field<bool>("_ready"));
                var tree = Field<Forms.TreeView>("_localTree");
                tree.SelectedNode = tree.Nodes.Find("LocalFolder:inbox", true).Single();
                Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == 14);
                Check(Field<IReadOnlyList<object>>("_rows").OfType<MessageSummary>().Select(m => m.Id).Order().SequenceEqual(copies), "Visible identities changed");
                main.Refresh(); Forms.Application.DoEvents();
                using var image = new System.Drawing.Bitmap(main.Width, main.Height);
                main.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, main.Width, main.Height));
                image.Save(Path.Combine(shots, "adoption-cleanup-" + stage + ".png"));
                main.Close();
            }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
