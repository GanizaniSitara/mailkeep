using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

internal static class ConnectedRefreshSmoke
{
    internal static async Task Run(string app, string shots)
    {
        if (!Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app)) throw new InvalidOperationException("Disposable guest fixture required");
        Directory.CreateDirectory(shots);
        await using var server = new ReceiveFixture(false);
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-connected-refresh-" + Guid.NewGuid().ToString("N"));
        var services = new MailkeepServices(root);
        services.Database.Initialize();
        services.Database.SetSetting("sync.auto", "off");
        var account = new MailAccount("connected", "custom", "connected@example.invalid", "INBOX", "Connected Inbox", "127.0.0.1", null,
            "fixture-password", new KeepAliveSettings(false), MailboxModes.Connected,
            new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", server.Port, MailSecurityModes.None));
        services.Accounts.Save([account]);
        var raw = Encoding.ASCII.GetBytes("From: fixture@example.invalid\r\nSubject: Independent Local Mail\r\n\r\nLocal copy remains intact.\r\n");
        var rawPath = services.Blobs.Put(raw);
        var localId = services.Database.Upsert(new("live", MailContract.LocalCopyAccount, "inbox", "copy:connected-refresh", null, 0,
            "Independent Local Mail", "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch, raw.Length, true, 0,
            "Local copy remains intact.", null, rawPath));
        Check((await services.Sync.SyncAsync(account.Name)).Ok, "Initial fake sync failed");
        Exception? failure = null;
        double invokeMs = 0;
        var pulses = 0;
        var thread = new Thread(() => { try { Exercise(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(75)), "Connected UI fixture timed out");
        if (failure is not null) throw new InvalidOperationException("Connected UI refresh failed", failure);
        var preserved = services.Blobs.ReadBytes(rawPath);
        Check(raw.SequenceEqual(preserved) && services.Database.GetMessage(localId) is not null, "Local Mail was changed");
        Check(server.UidCommands.All(command => command is "SEARCH" or "FETCH"), "Refresh wrote to the server");
        File.WriteAllText(Path.Combine(shots, "connected-refresh.json"), JsonSerializer.Serialize(new
        {
            root, invokeMs, pulses, connectedAfterEmptyRefresh = services.Database.ListMessages(account: account.Name).Count,
            localId, expectedRaw = Convert.ToBase64String(raw), preservedRaw = Convert.ToBase64String(preserved), serverWrites = 0
        }));
        Console.WriteLine($"PASS connected-refresh start={invokeMs:F1}ms heartbeat={pulses}; external move/read reflected with no new mail; held manual refreshes keep normal cursor and navigation; automatic read/removal changes displayed with zero arrivals; manual refresh handles empty Inbox; Local Mail bytes unchanged; server writes=0");

        void Exercise()
        {
            Forms.Application.EnableVisualStyles();
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var type = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(main)!;
            void Wait(Func<bool> ready, string message)
            {
                var clock = Stopwatch.StartNew();
                while (!ready()) { Check(clock.Elapsed.TotalSeconds < 15, message); Forms.Application.DoEvents(); Thread.Sleep(10); }
            }
            main.Show();
            Wait(() => Field<bool>("_ready"), "Main window not ready");
            var checkedAccounts = Field<Dictionary<string, DateTimeOffset>>("_accountChecked");
            checkedAccounts[account.Name] = DateTimeOffset.UtcNow;
            var accounts = Field<Forms.ListView>("_accounts");
            accounts.Items.Cast<Forms.ListViewItem>().Single(item => item.Tag is MailAccount saved && saved.Name == account.Name).Selected = true;
            var list = Field<Forms.ListView>("_messages");
            Wait(() => list.VirtualListSize == 2 && Field<MessageContent?>("_currentContent")?.Summary.Account == account.Name,
                "Connected cache did not load");
            checkedAccounts.Clear();
            server.Uids = [2]; server.SeenUids.Add(2);
            using var timer = new Forms.Timer { Interval = 20 };
            timer.Tick += (_, _) => pulses++;
            using var blocker = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
            blocker.Open();
            using var transaction = blocker.BeginTransaction();
            Task? refresh = null;
            try
            {
                timer.Start();
                var clock = Stopwatch.StartNew();
                refresh = (Task)type.GetMethod("CheckConnectedAccountAsync", flags)!.Invoke(main, [account])!;
                invokeMs = clock.Elapsed.TotalMilliseconds;
                Check(invokeMs < 250, "Checking a connected account blocked its UI callback for " + invokeMs + " ms");
                clock.Restart();
                Wait(() => clock.ElapsedMilliseconds >= 650, "Held-store heartbeat did not settle");
                Check(pulses >= 10 && !refresh.IsCompleted, "Connected refresh did not keep pumping while the store was held");
                Check(Field<MessageContent?>("_currentContent") is not null, "Held refresh blanked the existing letter");
            }
            finally { transaction.Rollback(); timer.Stop(); }
            Wait(() => refresh!.IsCompleted, "Connected refresh did not finish after releasing the store");
            refresh!.GetAwaiter().GetResult();
            Wait(() => list.VirtualListSize == 1, "External move with zero new messages did not refresh the displayed Inbox");
            Check(Field<IReadOnlyList<object>>("_rows").OfType<MessageSummary>().Single().IsRead, "External read flag not displayed");
            Wait(() => Field<MessageContent?>("_currentContent")?.Summary.ServerUid == 2
                && Field<Forms.Label>("_subject").Text == "Download fixture 2", "Removed message remained in the reading pane");
            Capture("connected-refresh-after-move");
            foreach (var all in new[] { false, true })
            {
                server.HoldFlags = true;
                server.FlagsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                server.ReleaseFlags = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var method = all ? "SyncAccountsAsync" : "SyncOneAsync";
                var manual = (Task)type.GetMethod(method, flags)!.Invoke(main, [all ? (object)true : account])!;
                try
                {
                    Wait(() => server.FlagsStarted.Task.IsCompleted, "Manual refresh did not reach the held server");
                    Check(!main.UseWaitCursor && !list.UseWaitCursor && !accounts.UseWaitCursor,
                        method + " applied a window-wide wait cursor during background refresh");
                    Check(list.Enabled && accounts.Enabled, "Refresh disabled the message or account list");
                    var tree = Field<Forms.TreeView>("_localTree");
                    tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Inbox, true).Single();
                    Wait(() => Field<MessageContent?>("_currentContent")?.Summary.Id == localId,
                        "Could not navigate to and read Local Mail while refresh was held");
                    Check(!manual.IsCompleted, "Held manual refresh unexpectedly completed");
                    Capture(all ? "connected-refresh-all-interactive" : "connected-refresh-one-interactive");
                }
                finally { server.HoldFlags = false; server.ReleaseFlags.TrySetResult(); }
                Wait(() => manual.IsCompleted, "Manual refresh did not finish after releasing the server");
                manual.GetAwaiter().GetResult();
                checkedAccounts[account.Name] = DateTimeOffset.UtcNow;
                accounts.Items.Cast<Forms.ListViewItem>().Single(item => item.Tag is MailAccount saved && saved.Name == account.Name).Selected = true;
                Wait(() => Field<MessageContent?>("_currentContent")?.Summary.ServerUid == 2,
                    "Could not return to Connected Inbox after refresh");
            }
            services.Accounts.Save([account with { KeepAlive = new KeepAliveSettings(true, 1) }]);
            server.SeenUids.Clear();
            AutomaticRefresh();
            Check(!Field<IReadOnlyList<object>>("_rows").OfType<MessageSummary>().Single().IsRead,
                "Automatic refresh with zero arrivals did not display the server read-state change");
            Capture("connected-refresh-automatic-read");
            server.Uids = [];
            AutomaticRefresh();
            Check(list.VirtualListSize == 0 && Field<MessageContent?>("_currentContent") is null,
                "Automatic refresh with zero arrivals left removed mail visible");
            Capture("connected-refresh-automatic-empty");
            server.Uids = [];
            var menu = main.MainMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "&View");
            menu.DropDownItems.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "&Refresh").PerformClick();
            Wait(() => list.VirtualListSize == 0 && !Field<bool>("_syncRunning"), "View Refresh did not reconcile an empty server Inbox");
            Capture("connected-refresh-empty");
            main.Close();

            void AutomaticRefresh()
            {
                services.Database.SetSyncState(account.Name, account.Mailbox, server.UidValidity, 2, DateTimeOffset.UtcNow.AddDays(-2));
                var automatic = (Task)type.GetMethod("AutoSyncTickAsync", flags)!.Invoke(main, null)!;
                Wait(() => automatic.IsCompleted, "Automatic refresh did not finish");
                automatic.GetAwaiter().GetResult();
                Check(!main.UseWaitCursor && list.Enabled && accounts.Enabled, "Automatic refresh changed the interactive state");
            }

            void Capture(string name)
            {
                using var image = new System.Drawing.Bitmap(main.Width, main.Height);
                main.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, main.Width, main.Height));
                image.Save(Path.Combine(shots, name + ".png"));
            }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
