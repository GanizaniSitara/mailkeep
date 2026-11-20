using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

/// <summary>Delayed portable writer with real pane selection and filing, on synthetic guest mail only.</summary>
internal static class StorageContentionSmoke
{
    public static int Run(string app, string shots)
    {
        if (!Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app)) throw new InvalidOperationException("Insiders fixture required");
        Directory.CreateDirectory(shots);
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-storage-contention-" + Guid.NewGuid().ToString("N"));
        var services = new MailkeepServices(root);
        var db = services.Database;
        db.Initialize(); db.SetSetting("sync.auto", "off");
        for (var i = 1; i <= 2; i++)
        {
            var raw = Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: Contention {i}\r\n\r\nReading stays responsive {i}\r\n");
            db.Upsert(new("live", MailContract.LocalCopyAccount, "inbox", "copy:contention:" + i, null, 0,
                "Contention " + i, "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddMinutes(i),
                raw.Length, true, 0, "Reading stays responsive " + i, null, services.Blobs.Put(raw)));
        }
        var migration = new BulkFolderMigration(services.Paths);
        migration.Run(migration.Preview().Confirmation, publish: true);
        string uuid;
        using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT uuid FROM bulk_bindings WHERE folder='inbox'";
            uuid = (string)q.ExecuteScalar()!;
        }
        Exception? failure = null;
        var thread = new Thread(() => { try { Exercise(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "Contention UI timeout");
        if (failure is not null) throw new InvalidOperationException("Storage contention UI failed", failure);
        Check(db.ListMessages().Count == 2 && db.GetMessage(2)!.Summary.Folder == "deleted", "Committed location/count differs");
        Check(migration.RebuildProjection() == 2, "Recovery rebuild changed occurrence count");
        File.WriteAllText(Path.Combine(shots, "storage-contention.json"), JsonSerializer.Serialize(new
        { pass = true, root, checks = "message body and folder navigation while writer blocked; UI pumps beyond old 5s timeout; queued filing completes; projection rebuild retains two messages" }));
        Console.WriteLine("PASS storage-contention read/navigation while blocked; queued UI move >5s; recovery/rebuild=2");
        return 0;

        void Exercise()
        {
            Forms.Application.EnableVisualStyles(); Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var type = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(main)!;
            void Wait(Func<bool> ready, double seconds = 20)
            {
                var clock = Stopwatch.StartNew();
                while (!ready()) { Check(clock.Elapsed.TotalSeconds < seconds, "UI condition timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
            }
            main.Show(); Wait(() => Field<bool>("_ready"));
            var tree = Field<Forms.TreeView>("_localTree");
            var list = Field<Forms.ListView>("_messages");
            tree.SelectedNode = tree.Nodes.Find("LocalFolder:inbox", true).Single();
            Wait(() => list.VirtualListSize == 2);
            using var blocker = new SqliteConnection($"Data Source={Path.Combine(root, "bound-folders", uuid, FolderBundle.FileName)};Pooling=False");
            blocker.Open(); using var transaction = blocker.BeginTransaction();
            using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
            {
                c.Open(); using var q = c.CreateCommand(); q.CommandText = "UPDATE messages SET is_read=0 WHERE id=1"; q.ExecuteNonQuery();
            }
            var writer = Task.Run(() => db.FolderStorage.Flush());
            Task? filing = null;
            try
            {
                Wait(() =>
                {
                    try { using var probe = new FileStream(Path.Combine(root, "bound-folders", ".operation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                    catch (FileNotFoundException) { }
                    catch (IOException e) when ((e.HResult & 0xffff) == 32) { return true; }
                    return false;
                }, 3);
                tree.SelectedNode = tree.Nodes.Find("LocalFolder:deleted", true).Single();
                Wait(() => list.VirtualListSize == 0, 2);
                tree.SelectedNode = tree.Nodes.Find("LocalFolder:inbox", true).Single();
                Wait(() => list.VirtualListSize == 2, 2);
                var rows = Field<IReadOnlyList<object>>("_rows").OfType<MessageSummary>().ToArray();
                var index = Array.FindIndex(rows, m => m.Id == 2);
                Check(rows[index].IsLocalStoredCopy, "Fixture must seed fileable local-copy provenance");
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(index); list.Items[index].Focused = true;
                Wait(() => Field<Forms.RichTextBox>("_body").Text.Contains("Reading stays responsive 2"), 2);
                filing = (Task)type.GetMethod("FileMessagesAsync", flags)!.Invoke(main, [new[] { rows[index] }, "deleted", "Deleted Items"])!;
                var clock = Stopwatch.StartNew();
                Wait(() => clock.Elapsed.TotalSeconds >= 6, 8);
                Check(!filing.IsCompleted, "Filing failed instead of waiting beyond five seconds");
                Check(!Field<Task>("_layoutWrite").IsCompleted, "Fixture did not exercise deferred layout persistence under the writer lock");
                using var image = new System.Drawing.Bitmap(main.Width, main.Height);
                main.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, main.Width, main.Height));
                image.Save(Path.Combine(shots, "storage-writer-blocked.png"));
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(shots, "storage-contention-failure.txt"), error.ToString());
                throw;
            }
            finally
            {
                transaction.Rollback();
                Wait(() => writer.IsCompleted, 10); writer.GetAwaiter().GetResult();
                if (filing is not null) { Wait(() => filing.IsCompleted, 10); filing.GetAwaiter().GetResult(); }
                main.Close();
            }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
