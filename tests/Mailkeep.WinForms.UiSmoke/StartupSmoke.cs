using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

internal static class StartupSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string app, string root, MailkeepServices services, string? screenshots = null)
    {
        services.Database.SetSetting("sync.auto", "off");
        services.Accounts.Save([]);
        services.Identities.Save([]);
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "startup regression timed out");
        if (failure is not null)
        {
            Console.Error.WriteLine(failure); // Keep the original failure if WebView profile cleanup also fails.
            throw new InvalidOperationException("startup regression failed", failure);
        }
        Console.WriteLine("PASS startup busy-store=responsive large-archive=250000 restored-folder=Inbox HTML=runtime-ready");

        void RunUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            NativeLibrary.Load(loader);
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
            var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            Task writer = Task.CompletedTask;
            var watch = Stopwatch.StartNew();
            // Warm the real browser first, so a slow browser cannot conceal the UI-thread database wait.
            using (var warmup = Open()) { Until(() => HtmlReady(warmup)); warmup.Close(); }
            using var acquired = new ManualResetEventSlim();
            writer = Task.Run(() =>
            {
                using var connection = Connection();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES('fixture.writer','held')";
                command.ExecuteNonQuery();
                acquired.Set();
                Thread.Sleep(5000);
                transaction.Rollback();
            });
            Check(acquired.Wait(5000), "fixture writer did not acquire lock");
            watch.Restart();
            double last = 0, longest = 0;
            var ticks = 0;
            using var timer = new Forms.Timer { Interval = 20 };
            timer.Tick += (_, _) => { var now = watch.Elapsed.TotalMilliseconds; longest = Math.Max(longest, now - last); last = now; ticks++; };
            timer.Start();
            using (var busy = Open(requireEarlyReveal: true))
            {
                timer.Stop();
                writer.GetAwaiter().GetResult();
                Console.WriteLine($"CHECK startup busy-store ready_ms={watch.ElapsedMilliseconds} heartbeat_ticks={ticks} longest_gap_ms={longest:F0}");
                Check(ticks >= 30 && longest < 750, "startup blocked the window while the database writer was active");
                Until(() => HtmlReady(busy));
                busy.Close();
            }
            using (var connection = Connection())
            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<250000)
                    INSERT INTO messages(source,account,folder,source_key,subject,sender,date_utc,is_read,body_text)
                    SELECT 'archive','archive-fixture',CASE WHEN i<=20 THEN 'Inbox' ELSE 'Folder '||(i%100) END,
                           'synthetic-'||i,'Startup fixture','fixture@example.invalid','2026-10-09T12:00:00Z',i%2,
                           'Disposable startup body' FROM n;
                    """;
                seed.ExecuteNonQuery();
            }
            services.Database.SetSetting("window.layout", "{\"SelectedFolder\":\"local:inbox\"}");
            watch.Restart(); last = 0; longest = 0; ticks = 0; timer.Start();
            using (var large = Open())
            {
                Until(() => Field<Forms.ListView>(large, "_messages").VirtualListSize == 20);
                Until(() => HtmlReady(large));
                timer.Stop();
                var tree = Field<Forms.TreeView>(large, "_localTree");
                Check(tree.SelectedNode?.Text.StartsWith("Inbox") == true, "Inbox selection was not restored");
                Check(tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Inbox, true).Single().Text.Contains("10"), "Inbox unread count was not restored");
                Console.WriteLine($"CHECK startup large-archive ready_ms={watch.ElapsedMilliseconds} longest_gap_ms={longest:F0} folders={services.Database.ListLocalFolders().Count}");
                Check(longest < 750, "large archive startup froze the window");
                large.Close();
            }

            Forms.Form Open(bool requireEarlyReveal = false)
            {
                var form = (Forms.Form)Activator.CreateInstance(type, [root])!;
                form.Show();
                Until(() => form.Opacity == 1);
                var opening = Geometry(form);
                if (requireEarlyReveal)
                {
                    Check(watch.ElapsedMilliseconds < 1000, "opening window waited for the slow-start fallback");
                    Check(!writer.IsCompleted && !Field<bool>(form, "_ready"), "window stayed hidden until the busy store finished");
                    Check(!Field<Forms.ToolStripItem>(form, "_receive").Enabled, "receive was enabled before the mailbox was ready");
                    Check(!Field<Forms.TreeView>(form, "_localTree").Enabled
                        && !Field<Forms.ListView>(form, "_accounts").Enabled
                        && !Field<Forms.ListView>(form, "_messages").Enabled, "mail selection was enabled before the mailbox was ready");
                    Check(Field<Forms.ToolStripStatusLabel>(form, "_status").Text.Contains("Opening"), "opening status was missing");
                    Console.WriteLine($"CHECK startup early-reveal_ms={watch.ElapsedMilliseconds} ready=false busy-store=held");
                }
                Capture(form, requireEarlyReveal ? "busy-opening" : "opening");
                Until(() => Field<bool>(form, "_ready"));
                Forms.Application.DoEvents();
                Check(Field<Forms.TreeView>(form, "_localTree").Enabled
                    && Field<Forms.ListView>(form, "_accounts").Enabled
                    && Field<Forms.ListView>(form, "_messages").Enabled, "mail selection stayed disabled after startup");
                if (requireEarlyReveal) Check(opening == Geometry(form), "saved panes moved after the opening window appeared");
                Capture(form, requireEarlyReveal ? "busy-ready" : "ready");
                return form;
            }
            string Geometry(Forms.Form form) => string.Join("|", form.Bounds,
                Field<Forms.SplitContainer>(form, "_mainSplit").SplitterDistance,
                Field<Forms.SplitContainer>(form, "_folderSplit").SplitterDistance,
                Field<Forms.SplitContainer>(form, "_previewSplit").SplitterDistance);
            void Capture(Forms.Form form, string phase)
            {
                if (screenshots is null) return;
                Directory.CreateDirectory(screenshots);
                form.Update();
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    var dc = graphics.GetHdc();
                    try { Check(PrintWindow(form.Handle, dc, 2), "startup window capture failed"); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                bitmap.Save(Path.Combine(screenshots, "startup-" + phase + ".png"));
            }
            T Field<T>(Forms.Form form, string name) => (T)type.GetField(name, Flags)!.GetValue(form)!;
            bool HtmlReady(Forms.Form form)
            {
                var html = Field<Forms.Control>(form, "_html");
                return html.GetType().GetProperty("CoreWebView2")!.GetValue(html) is not null;
            }
            SqliteConnection Connection()
            {
                var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = services.Paths.DatabasePath, Pooling = false }.ToString());
                connection.Open(); return connection;
            }
        }
    }

    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { Check(clock.Elapsed < TimeSpan.FromSeconds(30), "startup condition timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
