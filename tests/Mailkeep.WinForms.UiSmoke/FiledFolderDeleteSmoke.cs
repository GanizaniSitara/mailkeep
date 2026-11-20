using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Mailkeep.TestFixtures;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

internal static class FiledFolderDeleteSmoke
{
    public static async Task<int> Run(string app, string? screenshots)
    {
        Check(Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app), "Insiders VM fixture required");
        await using var receive = new ReceiveFixture(true) { HoldDownload = true };
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-filed-delete", Guid.NewGuid().ToString("N"));
        var services = new MailkeepServices(root); var db = services.Database; db.Initialize(); db.SetSetting("sync.auto", "off");
        services.Accounts.Save([new MailAccount("local:fixture@example.invalid", "custom", "fixture@example.invalid", "INBOX",
            LocalFolderKinds.Inbox, "127.0.0.1", null, "fixture", new KeepAliveSettings(), MailboxModes.LocalKeep,
            new IncomingServerSettings(MailProtocols.Pop3, "127.0.0.1", receive.Port, MailSecurityModes.None))]);
        db.SetSetting("pop_baseline:local:fixture@example.invalid", "fixture");
        var parent = db.CreateLocalFolder("OE6 Folders", null);
        var friends = db.CreateLocalFolder("Friends", parent.Key, "archive", "OE|OE6 Folders/Friends");
        var todos = db.CreateLocalFolder("_Pinned", null); var followup = db.CreateLocalFolder("2027", todos.Key);
        var empty = db.CreateLocalFolder("Empty deletion fixture", null);
        db.CreateArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        db.CreateArchiveFolder("archive", "Secondary archive", "Parent/A empty");
        db.CreateArchiveFolder("archive", "Secondary archive", "Parent/Z retained");
        var originals = new Dictionary<long, byte[]>();
        for (var i = 0; i < 10; i++)
        {
            var raw = Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: Friend {i}\r\nMessage-ID: <friend-{i}@example.invalid>\r\n\r\nFollowup {i}\r\n");
            var id = db.Upsert(new("archive", "OE", "OE6 Folders/Friends", "original:" + i, null, 0, "Friend " + i,
                "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddDays(i), raw.Length, i % 2 == 0, 0, "Followup " + i, null, services.Blobs.Put(raw)));
            originals.Add(id, raw);
        }
        db.AdoptMessages(originals.Keys.Take(5).ToArray(), followup.Key);
        var copies = db.AdoptedCopies(originals.Keys.ToArray()).ToDictionary(p => p.Key, p => p.Value.Id);
        var retainedBytes = new Dictionary<long, byte[]>(originals);
        foreach (var original in originals.Skip(5))
        {
            var i = originals.Keys.ToList().IndexOf(original.Key);
            byte[] raw = i == 8 ? [.. original.Value, 13, 10, 13, 10]
                : i == 9 ? [0xef, 0xbb, 0xbf, .. original.Value] : original.Value;
            retainedBytes[original.Key] = raw;
            copies[original.Key] = db.Upsert(new("live", "local:fixture@example.invalid", followup.Key, "received:" + i,
                null, 0, "Friend " + i, "fixture@example.invalid", "Fixture", [], DateTimeOffset.UnixEpoch.AddDays(i),
                raw.Length, true, 0, "Followup " + i, null, services.Blobs.Put(raw)));
        }
        var shots = screenshots ?? Path.Combine(root, "shots"); Directory.CreateDirectory(shots);
        Ui("filed", true);
        db.CreateArchiveFolder("archive", "OE", "OE6 Folders/Friends");
        Check(!db.ListLocalFolders().Any(f => f.Key == friends.Key), "Mirror survived deletion");
        Check(!db.FolderCounts("archive").Any(f => f.Folder == "OE6 Folders/Friends"), "Imported declaration returned");
        var audited = new List<object>();
        foreach (var original in originals)
        {
            var message = db.GetMessage(original.Key)!;
            Check(message.Summary.Id == copies[original.Key] && message.Summary.Folder == followup.Key, "Old reference lost");
            Check(File.ReadAllBytes(message.RawPath!).SequenceEqual(retainedBytes[original.Key]), "Retained MIME changed");
            using var provenance = JsonDocument.Parse(db.GetSetting("folder-storage.origins:" + message.Summary.Id)!);
            var origin = provenance.RootElement[0];
            var originalRaw = origin.GetProperty("RawBase64").ValueKind == JsonValueKind.Null
                ? File.ReadAllBytes(message.RawPath!) : Convert.FromBase64String(origin.GetProperty("RawBase64").GetString()!);
            Check(originalRaw.SequenceEqual(original.Value), "Original MIME not preserved");
            audited.Add(new { id = original.Key, expected = Convert.ToBase64String(retainedBytes[original.Key]),
                retained = Convert.ToBase64String(File.ReadAllBytes(message.RawPath!)),
                original = Convert.ToBase64String(original.Value), provenance = Convert.ToBase64String(originalRaw) });
        }
        Ui("deleted", false); Ui("restarted", false);
        File.WriteAllText(Path.Combine(shots, "filed-folder-delete-result.json"), JsonSerializer.Serialize(new { pass = true, count = 10, audited,
            checks = "Delete key folder routing; native confirmation defaults No; No preserves folder; Yes deletes; standard folders protected; both records removed; exact bytes and references retained; reimport guard; restart" }));
        Console.WriteLine("PASS filed-folder-delete: five adopted plus five received copies retained; BOM and trailing-line originals preserved; empty source removed; restart and reimport guard"); return 0;

        void Ui(string stage, bool expectFriends)
        {
            Exception? failure = null; var thread = new Thread(() => { try { Exercise(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); Check(thread.Join(TimeSpan.FromSeconds(90)), "UI timed out");
            if (failure is not null) throw new InvalidOperationException(stage, failure);
            void Exercise()
            {
                Forms.Application.EnableVisualStyles(); Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var type = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(main)!;
                void Wait(Func<bool> ready) { var clock = Stopwatch.StartNew(); while (!ready()) { Check(clock.Elapsed.TotalSeconds < 25, "UI did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); } }
                main.Show(); Wait(() => Field<bool>("_ready")); var tree = Field<Forms.TreeView>("_localTree");
                if (!expectFriends) Check(!ArchiveNodes("Parent/A empty").Any(), "Deleted archive folder returned after reopening");
                Check((tree.Nodes.Find("LocalFolder:" + friends.Key, true).Length != 0) == expectFriends, "Friends tree mismatch");
                if (expectFriends) { tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + friends.Key, true).Single(); Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == 0); }
                tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + followup.Key, true).Single();
                Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == 10);
                main.Refresh(); Forms.Application.DoEvents(); using var image = new System.Drawing.Bitmap(main.Width, main.Height);
                main.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, main.Width, main.Height)); image.Save(Path.Combine(shots, "filed-folder-delete-" + stage + ".png"));
                if (expectFriends)
                {
                    var receiving = (Task)type.GetMethod("SyncAccountsAsync", flags)!.Invoke(main, [false])!;
                    Wait(() => receive.DownloadStarted.Task.IsCompleted);
                    try
                    {
                        tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + friends.Key, true).Single();
                        Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == 0);
                        Confirm(Forms.Keys.Delete, false);
                        Check(db.ListLocalFolders().Any(f => f.Key == friends.Key), "No deleted the folder");
                        Confirm(Forms.Keys.Control | Forms.Keys.D, false);
                        Confirm(Forms.Keys.Shift | Forms.Keys.Delete, false);
                        var pendingMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        type.GetField("_filing", flags)!.SetValue(main, pendingMove.Task);
                        var waitingPulses = 0;
                        using var moveHeartbeat = new Forms.Timer { Interval = 20 };
                        moveHeartbeat.Tick += (_, _) => waitingPulses++;
                        try
                        {
                            moveHeartbeat.Start();
                            Confirm(Forms.Keys.Delete, true);
                            var heldMove = Stopwatch.StartNew();
                            while (heldMove.ElapsedMilliseconds < 650) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                            Check(Field<bool>("_folderChanging") && db.ListLocalFolders().Any(f => f.Key == friends.Key),
                                "Folder deletion overtook an unfinished message move");
                            Check(waitingPulses >= 5, "Waiting for the message move blocked UI events");
                            Check(Field<Forms.ToolStripStatusLabel>("_status").Text.Contains("message change"),
                                "Folder deletion did not explain its wait for the message move");
                            using var waiting = new System.Drawing.Bitmap(main.Width, main.Height);
                            main.DrawToBitmap(waiting, new System.Drawing.Rectangle(0, 0, main.Width, main.Height));
                            waiting.Save(Path.Combine(shots, "folder-delete-waiting-for-move.png"));
                            Console.WriteLine($"FOLDER DELETE FILING ORDER held_ms=650 ui_pulses={waitingPulses}");
                        }
                        finally { pendingMove.TrySetResult(); moveHeartbeat.Stop(); }
                        Wait(() => !db.ListLocalFolders().Any(f => f.Key == friends.Key) && !Field<bool>("_folderChanging"));
                        tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Inbox, true).Single();
                        tree.Focus(); Press(Forms.Keys.Delete);
                        Check(db.ListHeads("live", folder: followup.Key).Count == 10, "Tree Delete affected messages");
                        var retainedNode = tree.Nodes.Find("LocalFolder:" + followup.Key, true).Single();
                        tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + empty.Key, true).Single();
                        var pulses = 0; using var heartbeat = new Forms.Timer { Interval = 20 };
                        heartbeat.Tick += (_, _) => pulses++; heartbeat.Start();
                        var deletion = Stopwatch.StartNew();
                        Confirm(Forms.Keys.Delete, true);
                        Wait(() => !Field<bool>("_folderChanging"));
                        heartbeat.Stop();
                        Check(!db.ListLocalFolders().Any(folder => folder.Key == empty.Key), "Empty local folder survived deletion");
                        Check(tree.Nodes.Find("LocalFolder:" + empty.Key, true).Length == 0, "Deleted local node remained visible");
                        Check(ReferenceEquals(retainedNode, tree.Nodes.Find("LocalFolder:" + followup.Key, true).Single()), "Empty deletion rebuilt unrelated sidebar nodes");
                        Console.WriteLine($"EMPTY LOCAL DELETE elapsed_ms={deletion.ElapsedMilliseconds} ui_pulses={pulses}");
                        var archiveNode = ArchiveNode("Parent/A empty");
                        var neighbour = archiveNode.NextNode ?? archiveNode.PrevNode ?? archiveNode.Parent;
                        Check(neighbour is not null, "Archive deletion needs a neighbouring folder");
                        var survivingNode = ArchiveNode("Parent/Z retained");
                        tree.SelectedNode = archiveNode;
                        Wait(() => Field<Forms.ListView>("_messages").VirtualListSize == 0);
                        using var held = new SqliteConnection(new SqliteConnectionStringBuilder
                            { DataSource = services.Paths.DatabasePath, Pooling = false }.ToString());
                        held.Open(); using var transaction = held.BeginTransaction();
                        pulses = 0; heartbeat.Start();
                        Confirm(Forms.Keys.Delete, true);
                        var delay = Stopwatch.StartNew();
                        while (delay.ElapsedMilliseconds < 650) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                        Check(pulses >= 5, "Archive deletion blocked UI events on a held store");
                        Check(Field<bool>("_folderChanging"), "Held archive delete unexpectedly completed");
                        transaction.Commit();
                        Wait(() => !Field<bool>("_folderChanging")); heartbeat.Stop();
                        Check(archiveNode.TreeView is null, "Deleted archive node remained visible");
                        Check(ReferenceEquals(tree.SelectedNode, neighbour), "Archive deletion jumped away from its neighbour");
                        Check(ReferenceEquals(ArchiveNode("Parent/Z retained"), survivingNode), "Archive deletion rebuilt unrelated nodes");
                        Check(!db.FolderCounts("archive").Any(folder => folder.Account == "Secondary archive" && folder.Folder == "Parent/A empty"), "Archive declaration survived deletion");
                        main.Refresh(); Forms.Application.DoEvents();
                        using (var after = new System.Drawing.Bitmap(main.Width, main.Height))
                        {
                            main.DrawToBitmap(after, new System.Drawing.Rectangle(0, 0, main.Width, main.Height));
                            after.Save(Path.Combine(shots, "empty-archive-delete-neighbour.png"));
                        }
                        Console.WriteLine($"EMPTY ARCHIVE DELETE held_ms=650 ui_pulses={pulses} neighbour_preserved=True");
                        Check(!receiving.IsCompleted, "Fixture did not keep Local Mail receiving while deleting folders");
                        Console.WriteLine("PASS folder deletion completed while Local Mail download was still held");
                    }
                    finally
                    {
                        receive.ReleaseDownload.TrySetResult();
                        Wait(() => receiving.IsCompleted);
                        receiving.GetAwaiter().GetResult();
                    }
                }
                main.Close();

                Forms.TreeNode ArchiveNode(string path) => ArchiveNodes(path).Single();
                IEnumerable<Forms.TreeNode> ArchiveNodes(string path)
                {
                    IEnumerable<Forms.TreeNode> Walk(Forms.TreeNodeCollection nodes)
                    {
                        foreach (Forms.TreeNode node in nodes)
                        { yield return node; foreach (var child in Walk(node.Nodes)) yield return child; }
                    }
                    return Walk(tree.Nodes).Where(node => node.Tag is { } tag
                        && tag.GetType().GetProperty("Account")?.GetValue(tag) as string == "Secondary archive"
                        && tag.GetType().GetProperty("ArchiveFolder")?.GetValue(tag) as string == path);
                }

                void Press(Forms.Keys key)
                {
                    var arguments = new object[] { Forms.Message.Create(tree.Handle, 0x100, (IntPtr)(int)(key & Forms.Keys.KeyCode), IntPtr.Zero), key };
                    Check((bool)type.GetMethod("ProcessCmdKey", flags)!.Invoke(main, arguments)!, "Delete was not handled");
                }
                void Confirm(Forms.Keys key, bool yes)
                {
                    tree.Focus(); Check(tree.ContainsFocus, "Folder tree lacks focus");
                    var observed = false; Exception? error = null;
                    using var timer = new Forms.Timer { Interval = 50 };
                    timer.Tick += (_, _) =>
                    {
                        var dialog = FindWindow("#32770", "Delete Folder");
                        if (dialog == IntPtr.Zero) return;
                        GetWindowThreadProcessId(dialog, out var pid); if (pid != Environment.ProcessId) return;
                        timer.Stop(); observed = true;
                        try
                        {
                            Check((SendMessage(dialog, 0x400, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xffff) == 7, "Confirmation must default to No");
                            var rect = new Rect(); GetWindowRect(dialog, ref rect);
                            using var picture = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
                            using (var graphics = System.Drawing.Graphics.FromImage(picture))
                            { var dc = graphics.GetHdc(); try { Check(PrintWindow(dialog, dc, 0), "Confirmation capture failed"); } finally { graphics.ReleaseHdc(dc); } }
                            picture.Save(Path.Combine(shots, "folder-delete-confirm-" + (yes ? "yes" : "no") + ".png"));
                        }
                        catch (Exception e) { error = e; }
                        SendMessage(GetDlgItem(dialog, yes && error is null ? 6 : 7), 0xf5, IntPtr.Zero, IntPtr.Zero);
                    };
                    timer.Start();
                    var clock = Stopwatch.StartNew();
                    Forms.SendKeys.SendWait(key switch
                    {
                        Forms.Keys.Delete => "{DELETE}",
                        Forms.Keys.Control | Forms.Keys.D => "^d",
                        Forms.Keys.Shift | Forms.Keys.Delete => "+{DELETE}",
                        _ => throw new InvalidOperationException("Unsupported fixture key")
                    });
                    Wait(() => observed); timer.Stop();
                    Console.WriteLine($"NATIVE FOLDER CONFIRM key={key} elapsed_ms={clock.ElapsedMilliseconds}");
                    Check(observed, "Delete key did not show folder confirmation"); if (error is not null) throw error;
                }
            }
        }
    }
    private static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, ref Rect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
