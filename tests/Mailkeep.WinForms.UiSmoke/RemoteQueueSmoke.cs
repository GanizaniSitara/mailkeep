using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;
using Forms = System.Windows.Forms;

/// <summary>Native Connected deletion while the loopback IMAP provider is slow and refusing commands.</summary>
internal static class RemoteQueueSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    internal static void Run(string app, string root, MailkeepServices services, string? screenshots,
        string? realAccount = null, string? cli = null)
    {
        using var serverLifetime = realAccount is null ? new ServerLifetime() : null;
        var server = serverLifetime?.Server;
        services.Database.SetSetting("sync.auto", "off");
        foreach (var existing in services.Database.ListMessages()) services.Database.RemoveMessage(existing.Id);
        using var real = realAccount is null ? null : new RealConnectedFixture(services, realAccount);
        var account = real?.Account ?? new MailAccount("remote-fixture", "custom", "fixture@example.invalid", "INBOX", "inbox",
            "127.0.0.1", null, "fixture-only", new KeepAliveSettings(false), MailboxModes.Connected,
            new IncomingServerSettings(MailProtocols.Imap, "127.0.0.1", server!.Port, MailSecurityModes.Auto));
        if (real is null) services.Accounts.Save([account]);
        for (var uid = 1; real is null && uid <= 20; uid++) services.Database.Upsert(new MessageInput(MailContract.LiveSource,
            account.Name, account.FolderLabel, $"42:{uid}", uid, 42, $"Queued deletion {uid}", "fixture@example.invalid",
            "Fixture", [], DateTimeOffset.UtcNow.AddMinutes(-uid), 10, uid % 2 == 0, 0, "Fixture", null, null));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
                var loader = Path.Combine(directory, "WebView2Loader.dll");
                if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                NativeLibrary.Load(loader);
                var type = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                main.Show();
                Until(() => (bool)type.GetField("_ready", Flags)!.GetValue(main)!, 30000);
                // Select the actual Connected view, avoiding an unrelated background fetch in this fixture.
                type.GetField("_selectedAccount", Flags)!.SetValue(main, account);
                type.GetField("_selectedArchive", Flags)!.SetValue(main, null);
                var load = (Task)type.GetMethod("LoadMessagesAsync", Flags)!.Invoke(main, null)!;
                Until(() => load.IsCompleted);
                load.GetAwaiter().GetResult();
                var list = (Forms.ListView)type.GetField("_messages", Flags)!.GetValue(main)!;
                Until(() => list.VirtualListSize == 20);
                var accounts = (Forms.ListView)type.GetField("_accounts", Flags)!.GetValue(main)!;
                var countRow = accounts.Items.Cast<Forms.ListViewItem>().Single(item => item.Tag is MailAccount configured && configured.Name == account.Name);
                Check(UnreadCount() == 10, "Fixture must start with 10 unread messages");
                var max = 0d;
                for (var index = 0; index < 20; index++)
                {
                    var rows = (IReadOnlyList<object>)type.GetField("_rows", Flags)!.GetValue(main)!;
                    var selected = (MessageSummary)rows[0];
                    var beforeTotal = TotalCount();
                    var beforeUnread = UnreadCount();
                    var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var watch = new Stopwatch();
                    using var confirm = new Forms.Timer { Interval = 5 };
                    confirm.Tick += (_, _) =>
                    {
                        var dialog = FindWindow("#32770", "Delete message");
                        if (dialog == IntPtr.Zero) return;
                        GetWindowThreadProcessId(dialog, out var pid);
                        if (pid != Environment.ProcessId) return;
                        confirm.Stop();
                        acknowledged.SetResult();
                        watch.Start();
                        SendMessage(GetDlgItem(dialog, 6), 0xF5, IntPtr.Zero, IntPtr.Zero);
                    };
                    confirm.Start();
                    var deletion = (Task)type.GetMethod("DeleteMessagesAsync", Flags)!.Invoke(main, [new[] { selected }, false])!;
                    // Confirmation time is human input, so measure from its acceptance.
                    Check(acknowledged.Task.IsCompleted, "Delete confirmation was not accepted");
                    Until(() => list.VirtualListSize == 19 - index);
                    max = Math.Max(max, watch.Elapsed.TotalMilliseconds);
                    Console.WriteLine($"Delete {index + 1}: visible-action {watch.Elapsed.TotalMilliseconds:F1} ms");
                    Check(list.Enabled && main.Enabled && !main.UseWaitCursor, "Connected deletion disabled the UI");
                    Check(list.VirtualListSize == 19 - index, "Deleted row remained visible");
                    Check(TotalCount() == beforeTotal - 1, "Connected folder total waited for the server");
                    Check(UnreadCount() == beforeUnread - (selected.IsRead ? 0 : 1), "Connected unread count waited for the server");
                    Check(countRow.SubItems[1].Text == TotalCount().ToString("N0"), "Connected badge retained its previous total");
                    Check(countRow.ToolTipText == $"{TotalCount():N0} messages, {UnreadCount():N0} unread", "Connected tooltip retained its previous unread count");
                    var messageCount = (Forms.ToolStripStatusLabel)type.GetField("_count", Flags)!.GetValue(main)!;
                    Check(messageCount.Text == $"{TotalCount():N0} messages, {UnreadCount():N0} unread", "Folder message/unread count is stale");
                    Until(() => deletion.IsCompleted);
                    deletion.GetAwaiter().GetResult();
                }
                Until(() => services.Database.RemoteStatus().Failed > 0, 30000);
                var status = (Forms.ToolStripStatusLabel)type.GetField("_remoteStatus", Flags)!.GetValue(main)!;
                Until(() => status.Text.Contains("retrying"), 30000);
                Check(TotalCount() == 0 && UnreadCount() == 0, "Offline counts did not reach zero");
                Until(() => status.Text.StartsWith("Server changes: 20 pending"));
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, main.Size));
                    bitmap.Save(Path.Combine(screenshots, "remote-queue-pending.png"));
                }
                if (real is not null) real.Reconnect();
                else { server!.Offline = false; server.MoveDelayMs = 25; }
                var recovery = Stopwatch.StartNew();
                Until(() => services.Database.RemoteStatus().Pending == 0, 60000);
                var verifying = Task.Run(() => real?.Verify() ?? server!.MovedUids.Count);
                Until(() => verifying.IsCompleted, 60000);
                var moved = verifying.GetAwaiter().GetResult();
                Check(moved == 20, "Server did not receive all 20 deletions");
                Check(max < 100, $"Delete action exceeded 100 ms: {max:F1} ms");
                Check(services.Database.RecentFetches(100).Any(entry => entry.Source.StartsWith("remote-queue:") && entry.Ok == true), "Queue completion missing from log");
                if (cli is not null)
                {
                    var start = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    foreach (var arg in new[] { "--root", root, "log", "--limit", "100" }) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!;
                    var log = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    Check(process.ExitCode == 0 && log.Contains("remote-queue:delete"), "CLI log did not report queue operations");
                    if (screenshots is not null) File.WriteAllText(Path.Combine(screenshots, "remote-queue-cli-log.json"), log);
                }
                var finishingWrite = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var saving = (Task)type.GetMethod("PersistRemoteChangeAsync", Flags)!.MakeGenericMethod(typeof(int))
                    .Invoke(main, [new Func<Task<int>>(() => finishingWrite.Task)])!;
                main.Close();
                Check(!main.IsDisposed, "Application exited before its local queue write was saved");
                finishingWrite.SetResult(1);
                Until(() => saving.IsCompleted && main.IsDisposed);
                Console.WriteLine($"PASS remote-queue provider={(real is null ? "loopback" : "real")} deletes=20 max-action-ms={max:F1} recovery-seconds={recovery.Elapsed.TotalSeconds:F2} server-uids={moved} UI=enabled total-unread-badges=immediate pending=20-offline retry-status=visible log=drained close=waited-for-local-write");

                int TotalCount() => ((IReadOnlyDictionary<string, (int Total, int New)>)type.GetField("_accountCounts", Flags)!.GetValue(main)!).GetValueOrDefault(account.Name).Total;
                int UnreadCount() => ((IReadOnlyDictionary<string, int>)type.GetField("_accountUnreadCounts", Flags)!.GetValue(main)!).GetValueOrDefault(account.Name);

                void Until(Func<bool> predicate, int timeout = 10000)
                {
                    var watch = Stopwatch.StartNew();
                    while (!predicate())
                    {
                        Check(watch.ElapsedMilliseconds < timeout, "Remote queue smoke timed out");
                        Forms.Application.DoEvents();
                        Thread.Sleep(1);
                    }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromMinutes(3)), "Remote queue thread timed out");
        if (failure is not null) throw new InvalidOperationException("Remote queue smoke failed", failure);
    }
    private sealed class ServerLifetime : IDisposable
    {
        public ImapFixture Server { get; } = new(false, 42, move: true) { Offline = true, MoveDelayMs = 500 };
        public void Dispose() => Server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
