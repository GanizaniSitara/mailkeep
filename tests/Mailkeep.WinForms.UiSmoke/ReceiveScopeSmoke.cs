using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Click the real receive controls in the guest, with disposable accounts and refused loopback ports.</summary>
internal static class ReceiveScopeSmoke
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    internal static void Run(string app, string root, MailkeepServices services)
    {
        services.Accounts.Save([
            new("fixture-local", "custom", "local@example.invalid", "INBOX", "inbox", "127.0.0.1", null, "unused",
                new KeepAliveSettings(false), MailboxModes.LocalKeep, new IncomingServerSettings(MailProtocols.Pop3, "127.0.0.1", 1)),
            new("fixture-connected", "custom", "connected@example.invalid", "INBOX", "remote", "127.0.0.1", null, "unused",
                new KeepAliveSettings(false), MailboxModes.Connected, new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", 1))
        ]);
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
                var loader = Path.Combine(directory, "WebView2Loader.dll");
                if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                NativeLibrary.Load(loader);
                var type = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                main.Show();
                Until(() => (bool)type.GetField("_ready", flags)!.GetValue(main)!);
                // Simulate another successful fetch committing mail while this pass will report a
                // refused account. The failure must not leave the displayed Inbox on its old rows.
                services.Database.Upsert(new MessageInput(MailContract.LiveSource, "fixture-local", LocalFolderKinds.Inbox,
                    "arrival-during-failed-pass", null, 0, "Arrived during failed pass", "sender@example.invalid", "Sender",
                    ["local@example.invalid"], DateTimeOffset.UtcNow.AddMinutes(1), 128, true, 0, "Received fixture", null, null));
                using var dismiss = new Forms.Timer { Interval = 100 };
                dismiss.Tick += (_, _) =>
                {
                    foreach (var title in new[] { "Checking Local accounts…", "Refreshing connected accounts…", "Checking Local accounts...", "Refreshing connected accounts..." })
                    {
                        var window = FindWindow("#32770", title);
                        if (window == IntPtr.Zero) continue;
                        GetWindowThreadProcessId(window, out var process);
                        if (process == Environment.ProcessId) SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    }
                };
                dismiss.Start();
                var receive = (Forms.ToolStripSplitButton)type.GetField("_receive", flags)!.GetValue(main)!;
                receive.PerformButtonClick();
                Complete(1);
                var rows = (IReadOnlyList<object>)type.GetField("_rows", flags)!.GetValue(main)!;
                Check(rows.OfType<MessageSummary>().Any(row => row.Subject == "Arrived during failed pass"), "failed receive left newly committed mail hidden");
                Check(services.Database.RecentFetches(10).All(fetch => fetch.Account == "fixture-local"), "Send/Receive checked Connected mail");
                var all = receive.DropDownItems.OfType<Forms.ToolStripMenuItem>().First(item => item.Text == "Send and Receive All Local");
                all.PerformClick();
                Complete(2);
                Check(services.Database.RecentFetches(10).All(fetch => fetch.Account == "fixture-local"), "All Local checked Connected mail");
                Forms.ToolStripItem? refresh = null;
                void Find(Forms.Control control)
                {
                    if (control is Forms.ToolStrip strip) refresh ??= strip.Items.Cast<Forms.ToolStripItem>().FirstOrDefault(item => item.Text == "Refresh Cnnct'd");
                    foreach (Forms.Control child in control.Controls) Find(child);
                }
                Find(main);
                Check(refresh is not null, "Connected refresh button missing");
                refresh!.PerformClick();
                Complete(3);
                var fetches = services.Database.RecentFetches(10);
                Check(fetches.Count(fetch => fetch.Account == "fixture-connected") == 1, "Connected refresh failed or checked Local mail");
                Check(fetches.Count(fetch => fetch.Account == "fixture-local") == 2, "Connected refresh checked Local mail");
                Check(services.Database.ListOutgoing().Count == 0, "Fixture created or sent outgoing mail");
                Console.WriteLine("PASS receive-scope button=local dropdown=local refresh=connected outgoing=0 loopback-only failed-pass-arrival-visible");

                void Complete(int count) => Until(() => services.Database.RecentFetches(10).Count == count
                    && !(bool)type.GetField("_syncRunning", flags)!.GetValue(main)!);
                void Until(Func<bool> condition)
                {
                    var clock = Stopwatch.StartNew();
                    while (!condition()) { Check(clock.ElapsedMilliseconds < 25000, "Receive scope fixture timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(100)), "Receive scope fixture timed out");
        if (failure is not null) throw new InvalidOperationException("Receive scope UI failed", failure);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
