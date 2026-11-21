using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;
using Forms = System.Windows.Forms;

/// <summary>Real POP/IMAP receive through the Local Mail window, with held transfers and an account failure.</summary>
internal static class DownloadProgressSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static async Task Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        await using var pop = new ReceiveFixture(true) { HoldDownload = true };
        await using var imap = new ReceiveFixture(false) { HoldDownload = true };
        await using var refused = new ReceiveFixture(true) { RejectLogin = true, HoldLogin = true };
        await using var cancelled = new ReceiveFixture(true) { HoldDownload = true };
        MailAccount Account(string name, bool isPop, int port) => new(name, "custom", name + "@example.invalid", "INBOX", LocalFolderKinds.Inbox,
            "127.0.0.1", null, "fixture-password", new KeepAliveSettings(), MailboxModes.LocalKeep,
            new IncomingServerSettings(isPop ? MailProtocols.Pop3 : MailProtocols.Imap, "127.0.0.1", port, MailSecurityModes.None));
        var accounts = new[] { Account("pop-receive", true, pop.Port), Account("imap-receive", false, imap.Port), Account("failed-receive", true, refused.Port) };
        services.Accounts.Save(accounts);
        services.Database.SetSetting("sync.auto", "off");
        services.Database.CreateArchiveFolder(MailContract.ArchiveSource, "merged-receive-fixture", "Inbox");
        services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "merged-receive-fixture", "Inbox", "old-import",
            null, 0, "Old merged receive fixture", "old@example.invalid", "Old", [], DateTimeOffset.UnixEpoch.AddYears(-1),
            128, true, 0, "Invented historical mail", null, null));
        for (var i = 0; i < 120; i++)
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, "pop-receive", LocalFolderKinds.Inbox,
                "receive-scroll-old-" + i, null, 0, "Older receive fixture " + i, "sender@example.invalid", "Sender", [],
                DateTimeOffset.UnixEpoch.AddMinutes(-i), 128, true, 0, "Invented older mail", null, null));
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "download progress test timed out");
        if (failure is not null) throw new InvalidOperationException("download progress failed", failure);
        Console.WriteLine("PASS download-progress POP=2 IMAP=2 errors=stay-open/error-tab hidden-errors=reopened success=auto-dismissed main=responsive screen=centred single=no-duplicates automatic=no-focus-theft no-server-deletion");

        void RunUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            NativeLibrary.Load(loader);
            var type = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            Task? receive = null;
            T Get<T>(string name) => (T)type.GetField(name, Flags)!.GetValue(main)!;
            Forms.Form Dialog() => Get<Forms.Form>("_downloadProgress");
            Forms.ListView Rows() => (Forms.ListView)Dialog().GetType().GetField("_accounts", Flags)!.GetValue(Dialog())!;
            bool Finished() => (bool)Dialog().GetType().GetProperty("Finished", Flags)!.GetValue(Dialog())!;
            try
            {
                main.Show(); Until(() => Get<bool>("_ready"));
                main.Location = new System.Drawing.Point(0, 0);
                var messages = Get<Forms.ListView>("_messages");
                Until(() => messages.VirtualListSize >= 120);
                messages.SelectedIndices.Clear(); messages.SelectedIndices.Add(60); messages.Items[60].Focused = true;
                messages.TopItem = messages.Items[55]; messages.Focus();
                PostMessage(messages.Handle, 0x100, (IntPtr)Forms.Keys.Down, (IntPtr)1);
                Until(() => Environment.TickCount64 - Get<long>("_lastMailInput") < 2000);
                Until(() => Get<MessageContent?>("_currentContent")?.Summary.Id
                    == ((MessageSummary)Get<IReadOnlyList<object>>("_rows")[messages.SelectedIndices[0]]).Id);
                var selectedBeforeReceive = Get<MessageContent>("_currentContent").Summary.Id;
                Get<Forms.RichTextBox>("_body").Focus(); Forms.Application.DoEvents();
                Check(Get<Forms.RichTextBox>("_body").ContainsFocus, "receive fixture reading pane lacks focus");
                var before = GetForegroundWindow();
                receive = (Task)type.GetMethod("SyncOneAsync", Flags)!.Invoke(main, [accounts[0]])!;
                Until(() => pop.DownloadStarted.Task.IsCompleted);
                pop.ReleaseDownload.TrySetResult(); Await(receive);
                var settled = Stopwatch.StartNew();
                while (settled.ElapsedMilliseconds < 500) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check(messages.TopItem?.Index == 0, "successful explicit receive with reading-pane focus did not retain arrival at top");
                Check(!Dialog().Visible && Get<MessageContent?>("_currentContent")?.Summary.Id == selectedBeforeReceive,
                    "successful receive changed preview or left progress open");
                messages.TopItem = messages.Items[55];
                receive = (Task)type.GetMethod("SyncAllAsync", Flags)!.Invoke(main, [])!;
                Until(() => pop.DownloadStarted.Task.IsCompleted && imap.DownloadStarted.Task.IsCompleted);
                Check(Dialog().Visible && !Dialog().Modal && main.Enabled, "progress missing or modal");
                var progress = (Forms.ProgressBar)Dialog().GetType().GetField("_progress", Flags)!.GetValue(Dialog())!;
                var tabs = (Forms.TabControl)Dialog().GetType().GetField("_tabs", Flags)!.GetValue(Dialog())!;
                Check(progress.Visible && tabs.TabPages.Count == 2, "receive progress or Tasks/Errors tabs missing");
                var area = Forms.Screen.FromControl(main).WorkingArea;
                Check(Dialog().Left == area.Left + (area.Width - Dialog().Width) / 2
                    && Dialog().Top == area.Top + (area.Height - Dialog().Height) / 2, "progress is not centred on Mailkeep's screen");
                Check(GetForegroundWindow() == before, "progress stole focus");
                Until(() => Rows().Items.Cast<Forms.ListViewItem>().Count(row => row.SubItems[2].Text == "0 of 2") == 1
                    && Rows().Items.Cast<Forms.ListViewItem>().Any(row => row.Name == "failed-receive" && row.SubItems[1].Text == "Signing in"));
                var pulses = 0;
                using var timer = new Forms.Timer { Interval = 20 };
                timer.Tick += (_, _) => pulses++;
                timer.Start(); var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 250) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check(pulses >= 3, "download blocked UI timers");
                main.Close(); Check(!main.IsDisposed, "main exited during a receive");
                Capture("download-progress-active");
                Dialog().Close(); Check(!Dialog().Visible && !Dialog().IsDisposed && !receive.IsCompleted, "closing progress stopped receive");
                type.GetMethod("ShowDownloadProgress", Flags)!.Invoke(main, []);
                Check(Dialog().Visible && Rows().Items.Count == 3, "reopening lost account rows");
                Dialog().Hide();
                pop.ReleaseDownload.TrySetResult(); imap.ReleaseDownload.TrySetResult();
                Until(() => Get<IReadOnlyList<object>>("_rows").OfType<MessageSummary>()
                    .Count(row => row.Subject.StartsWith("Download fixture ")) == 4);
                Check(!receive.IsCompleted && messages.TopItem?.Index == 0,
                    "healthy arrivals did not reach the merged Inbox top while another account was stalled");
                refused.ReleaseLogin.TrySetResult();
                Await(receive); Until(Finished);
                Check(messages.TopItem?.Index == 0, "explicit Send/Receive did not reveal arrivals after recent pre-receive input");
                Check(Get<MessageContent?>("_currentContent")?.Summary.Id == selectedBeforeReceive,
                    "explicit receive replaced the selected message preview");
                Check(Dialog().Visible && !Dialog().IsDisposed && tabs.SelectedIndex == 1, "failed receive did not reopen on Errors");
                type.GetMethod("ShowDownloadProgress", Flags)!.Invoke(main, []);
                Check(Dialog().Visible, "completed results cannot be reopened");
                Check(pop.Downloads == 2 && imap.Downloads == 2 && refused.Downloads == 0, "wrong fixture download counts");
                Check(Rows().Items.Cast<Forms.ListViewItem>().Count(row => row.SubItems[1].Text == "Complete" && row.SubItems[2].Text == "2 new") == 1
                    && Rows().Items.Cast<Forms.ListViewItem>().Count(row => row.SubItems[1].Text == "Complete" && row.SubItems[2].Text == "0 new") == 1,
                    "completed downloads missing");
                var failed = Rows().Items.Cast<Forms.ListViewItem>().Single(row => row.SubItems[1].Text == "Failed");
                failed.Selected = true; Forms.Application.DoEvents();
                var recordedError = services.Database.RecentFetches(10).Single(fetch => fetch.Account == "failed-receive").Error;
                Check(!string.IsNullOrWhiteSpace(recordedError) && failed.SubItems[3].Text == recordedError, "failure reason differs from fetch result");
                Check(progress.Value == 3 && tabs.TabPages[1].Text == "Errors (1)", "overall progress or error count missing");
                Capture("download-progress-finished");
                tabs.SelectedIndex = 1; Forms.Application.DoEvents();
                Check(((Forms.TextBox)Dialog().GetType().GetField("_details", Flags)!.GetValue(Dialog())!).Text.Contains(recordedError!), "error tab did not show its first error detail");
                Capture("download-progress-errors");
                var toggle = (Forms.Button)Dialog().GetType().GetField("_toggleDetails", Flags)!.GetValue(Dialog())!;
                toggle.PerformClick(); Check(!tabs.Visible, "details did not collapse");
                toggle.PerformClick(); Check(tabs.Visible, "details did not expand");
                tabs.SelectedIndex = 0;
                Dialog().StartPosition = Forms.FormStartPosition.Manual;
                Dialog().Location = new System.Drawing.Point(1920, -300);
                Dialog().Size = new System.Drawing.Size(620, 450);
                Forms.Application.DoEvents(); Capture("download-progress-portrait-secondary");
                var data = services.Database.ListMessages(limit: 100);
                Check(data.Count(row => accounts.Take(2).Any(account => account.Name == row.Account)
                    && row.Subject.StartsWith("Download fixture ")) == 4, "downloaded messages missing from Inbox");
                Dialog().Close(); Check(Dialog().IsDisposed, "finished dialog did not close");
                receive = (Task)type.GetMethod("SyncOneAsync", Flags)!.Invoke(main, [accounts[0]])!;
                Await(receive); Check(Finished() && !Dialog().Visible && Rows().Items.Count == 1 && Rows().Items[0].SubItems[2].Text == "0 new", "single-account result missing or not dismissed");
                Check(pop.Downloads == 2, "single-account pass downloaded duplicates");
                foreach (var account in accounts) services.Database.SetSyncState(account.Name, account.Mailbox, account.EffectiveProtocol == MailProtocols.Imap ? 1u : 0u,
                    account.EffectiveProtocol == MailProtocols.Imap ? 2u : 0u, DateTimeOffset.UtcNow.AddDays(-1));
                using var sentinel = new Forms.Form { Text = "Download test focus sentinel" };
                sentinel.Show(); sentinel.Activate(); Forms.Application.DoEvents(); before = GetForegroundWindow();
                receive = (Task)type.GetMethod("AutoSyncTickAsync", Flags)!.Invoke(main, [])!;
                Await(receive); Forms.Application.DoEvents();
                Console.WriteLine($"AUTOMATIC ERROR finished={Finished()} visible={Dialog().Visible} before={before} sentinel={sentinel.Handle} after={GetForegroundWindow()} active={Forms.Form.ActiveForm?.Text}");
                Check(Finished() && Dialog().Visible && GetForegroundWindow() == before && before == sentinel.Handle, "automatic error window missing or stole foreground");
                var cancelAccount = Account("cancel-receive", true, cancelled.Port);
                services.Accounts.Save([.. accounts, cancelAccount]);
                receive = (Task)type.GetMethod("SyncOneAsync", Flags)!.Invoke(main, [cancelAccount])!;
                Until(() => cancelled.DownloadStarted.Task.IsCompleted);
                var stop = (Forms.Button)Dialog().GetType().GetField("_stop", Flags)!.GetValue(Dialog())!;
                Check(stop.Visible && stop.Enabled, "Stop is unavailable during a held receive");
                var stopping = Stopwatch.StartNew(); stop.PerformClick(); Await(receive);
                Check(stopping.ElapsedMilliseconds < 5000 && Finished() && !Get<bool>("_syncRunning"), "Stop did not release the receive pass promptly");
                Check(!stop.Enabled && services.Database.ListMessages(limit: 500).All(row => row.Account != cancelAccount.Name), "Stop stored an incomplete message or remained enabled");
                Check(services.Database.ListMessages(limit: 500).Count(row => row.Subject.StartsWith("Download fixture ")) == 4,
                    "Stop removed mail received earlier");
                Capture("download-progress-stopped");
                services.Accounts.Save(accounts);
                receive = (Task)type.GetMethod("SyncOneAsync", Flags)!.Invoke(main, [accounts[0]])!;
                Await(receive); Check(!Get<bool>("_syncRunning") && !Dialog().Visible, "cannot receive again after Stop");
                var originals = services.Database.ListMessages(accounts[1].Name).ToDictionary(row => row.ServerUid!.Value);
                imap.UidValidity = 2;
                imap.Uids = [7, 8];
                imap.ImapMime[7] = ReceiveFixture.Message(2);
                receive = (Task)type.GetMethod("SyncOneAsync", Flags)!.Invoke(main, [accounts[1]])!;
                Await(receive);
                var afterReset = services.Database.ListMessages(accounts[1].Name);
                Check(afterReset.Count == 3 && afterReset.Single(row => row.Id == originals[1].Id).ServerUid is null
                    && afterReset.Single(row => row.Id == originals[2].Id).SourceKey == "2:7"
                    && afterReset.Single(row => row.ServerUid == 8).Subject == "Download fixture 8",
                    "UID reset lost local mail, duplicated an existing message, or missed the arrival");
                Until(() => Get<IReadOnlyList<object>>("_rows").OfType<MessageSummary>()
                    .Count(row => row.Account == accounts[1].Name) == 3);
                AddressingSmoke.Capture(main.Handle, screenshots, "download-uid-reset");
                Console.WriteLine("PASS UID reset: missing original retained, identical MIME rebound, new arrival shown");
                lock (pop.Commands) Check(!pop.Commands.Contains("DELE"), "POP deleted server mail");
                lock (imap.Commands) Check(!imap.Commands.Contains("STORE"), "IMAP changed server flags");
                Console.WriteLine("DOWNLOAD PROGRESS held-transfer UI pulses=" + pulses);
            }
            finally
            {
                pop.ReleaseDownload.TrySetResult(); imap.ReleaseDownload.TrySetResult();
                refused.ReleaseLogin.TrySetResult();
                if (receive is { IsCompleted: false }) Await(receive);
                Get<Forms.Form?>("_downloadProgress")?.Dispose();
                main.Close(); Forms.Application.DoEvents();
            }

            void Capture(string name)
            {
                if (screenshots is null) return;
                Directory.CreateDirectory(screenshots);
                using var image = new System.Drawing.Bitmap(Dialog().Width, Dialog().Height);
                Dialog().DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, image.Size));
                image.Save(Path.Combine(screenshots, name + ".png"));
            }
        }
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> condition) { var clock = Stopwatch.StartNew(); while (!condition()) { Check(clock.ElapsedMilliseconds < 15000, "download UI did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
