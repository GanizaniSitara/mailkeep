using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

/// <summary>A held private SQLite read must not stop the window repainting after a receive.</summary>
internal static class ReceiveRedrawSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string app, string root, MailkeepServices services)
    {
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(60)), "receive redraw test timed out");
        if (failure is not null) throw new InvalidOperationException("receive redraw failed", failure);
        Console.WriteLine("PASS receive-redraw held-count ui-responsive selection-preserved new-mail-visible no-arrival-list-preserved stale-counts-discarded");

        void RunUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            System.Runtime.InteropServices.NativeLibrary.Load(loader);
            var type = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            T Field<T>(string name) => (T)type.GetField(name, Flags)!.GetValue(main)!;
            void Until(Func<bool> condition)
            {
                var clock = Stopwatch.StartNew();
                while (!condition()) { Check(clock.ElapsedMilliseconds < 15000, "receive redraw did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); }
            }
            main.Show(); Until(() => Field<bool>("_ready") && Field<MessageContent?>("_currentContent") is not null);
            var list = Field<Forms.ListView>("_messages");
            var selected = ((MessageSummary)Field<IReadOnlyList<object>>("_rows")[list.SelectedIndices[0]]).Id;
            // DELETE journal mode makes a real database read wait behind our exclusive transaction.
            // This is a disposable fixture; no production settings or test-only app hooks are used.
            using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
            {
                c.Open(); using var q = c.CreateCommand(); q.CommandText = "PRAGMA journal_mode=DELETE"; q.ExecuteScalar();
            }
            foreach (var reload in new[] { true, false })
            {
                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var blocker = Task.Run(() =>
                {
                    using var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
                    c.Open(); using var q = c.CreateCommand(); q.CommandText = "BEGIN EXCLUSIVE"; q.ExecuteNonQuery();
                    held.Set();
                    try { release.Wait(TimeSpan.FromSeconds(3)); }
                    finally { q.CommandText = "ROLLBACK"; q.ExecuteNonQuery(); }
                });
                try
                {
                    Check(held.Wait(TimeSpan.FromSeconds(5)), "fixture lock not acquired");
                    var originalRows = Field<IReadOnlyList<object>>("_rows");
                    var clock = Stopwatch.StartNew();
                    var refresh = (Task)type.GetMethod("RefreshAfterReceiveAsync", Flags)!.Invoke(main, [reload])!;
                    var invocationMs = clock.ElapsedMilliseconds;
                    Check(invocationMs < 500, $"receive blocked the UI for {invocationMs}ms while recounting");
                    Check(!refresh.IsCompleted, "held read did not delay the refresh");
                    var pulses = 0;
                    using var timer = new Forms.Timer { Interval = 20 };
                    timer.Tick += (_, _) => pulses++;
                    timer.Start();
                    while (clock.ElapsedMilliseconds < 250) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                    Check(pulses >= 3, "UI timer stopped during recount");
                    Check(ReferenceEquals(originalRows, Field<IReadOnlyList<object>>("_rows")), "held refresh blanked or replaced the message list");
                    // A newer read-state refresh owns the counts; the pending older snapshot must not rebuild nodes.
                    var tree = Field<Forms.TreeView>("_localTree");
                    var node = tree.SelectedNode;
                    type.GetField("_readCountsGeneration", Flags)!.SetValue(main, Field<int>("_readCountsGeneration") + 1);
                    release.Set(); Until(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
                    Check(ReferenceEquals(node, tree.SelectedNode), "superseded counts rebuilt the tree");
                    Check(((MessageSummary)Field<IReadOnlyList<object>>("_rows")[list.SelectedIndices[0]]).Id == selected, "receive lost the selected message");
                    if (!reload) Check(ReferenceEquals(originalRows, Field<IReadOnlyList<object>>("_rows")), "no-arrival refresh replaced the message list");
                    Console.WriteLine($"RECEIVE REFRESH reload={reload} invocation_ms={invocationMs} ui_pulses={pulses}");
                }
                finally { release.Set(); blocker.GetAwaiter().GetResult(); }
            }
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
                "received-fixture", null, 0, "New received fixture", "sender@example.invalid", "Sender",
                ["local@example.invalid"], DateTimeOffset.UtcNow.AddMinutes(1), 128, true, 0, "New body", null, null));
            var arrival = (Task)type.GetMethod("RefreshAfterReceiveAsync", Flags)!.Invoke(main, [true])!;
            Until(() => arrival.IsCompleted); arrival.GetAwaiter().GetResult();
            Check(Field<IReadOnlyList<object>>("_rows").OfType<MessageSummary>().Any(row => row.Subject == "New received fixture"), "received mail did not appear");
            Check(((MessageSummary)Field<IReadOnlyList<object>>("_rows")[list.SelectedIndices[0]]).Id == selected, "arrival changed selection");
            main.Close();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
