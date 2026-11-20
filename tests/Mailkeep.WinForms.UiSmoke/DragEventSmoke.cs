using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

internal static class DragEventSmoke
{
    public static void Run(string app, string root, MailkeepServices services, bool bulkStorage = false, string? screenshots = null)
    {
        foreach (var folder in new[] { "Inbox", "Projects/Filed" })
            services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "archive-fixture", folder, folder, null, 0,
                "Imported " + folder, "archive@example.invalid", "Archive", [], DateTimeOffset.UtcNow.AddDays(-1),
                128, true, 0, "Imported body", null, null));
        for (var i = 0; i < 3; i++)
            services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "secondary-archive-fixture", i == 2 ? "Filed work" : "Imported work", "secondary:" + i, null, 0,
                "Secondary imported " + i, "archive@example.invalid", "Archive", [], DateTimeOffset.UnixEpoch.AddMinutes(i),
                128, true, 0, "Secondary imported body", null, null));
        if (bulkStorage)
        {
            if (!Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app))
                throw new InvalidOperationException("Bulk drag tests require the background VM.");
            using var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"); c.Open();
            foreach (var message in services.Database.ListMessages())
            {
                var content = services.Database.GetMessage(message.Id)!;
                if (content.RawPath is not null) continue;
                var raw = System.Text.Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: {message.Subject}\r\n\r\nFixture body {message.Id}\r\n");
                using var q = c.CreateCommand(); q.CommandText = "UPDATE messages SET raw_path=$raw,size=$size WHERE id=$id";
                q.Parameters.AddWithValue("$raw", services.Blobs.Put(raw)); q.Parameters.AddWithValue("$size", raw.Length);
                q.Parameters.AddWithValue("$id", message.Id); q.ExecuteNonQuery();
            }
            c.Dispose();
            var migration = new BulkFolderMigration(services.Paths);
            migration.Run(migration.Preview().Confirmation, publish: true);
        }
        var expectedSecondary = bulkStorage ? services.Database.ListMessages().Where(message => message.Account == "secondary-archive-fixture")
            .ToDictionary(message => message.Id, message => Convert.ToBase64String(File.ReadAllBytes(services.Database.GetMessage(message.Id)!.RawPath!))) : [];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckEvents(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("Drag event smoke did not finish.");
        if (failure is not null) throw new InvalidOperationException("Drag event smoke failed.", failure);

        void CheckEvents()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
            var type = assembly.GetType("Mailkeep.WinForms.MainForm", throwOnError: true)!;
            using var form = (Forms.Form)Activator.CreateInstance(type, [root])!;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            form.Show();
            var watch = Stopwatch.StartNew();
            while (!(bool)type.GetField("_ready", flags)!.GetValue(form)!)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Drag fixture did not initialize.");
                Forms.Application.DoEvents();
                Thread.Sleep(10);
            }
            var tree = (Forms.TreeView)type.GetField("_localTree", flags)!.GetValue(form)!;
            var list = (Forms.ListView)type.GetField("_messages", flags)!.GetValue(form)!;
            var messages = services.Database.ListMessages().Where(message => message.Account == "local-fixture").ToArray();
            var target = services.Database.ListLocalFolders().Single(folder => folder.Name == "Filed fixture");
            var merged = services.Database.ListLocalFolders().Single(folder => folder.OriginKey == "archive-fixture|Projects/Filed");
            Check(messages.Length == 2, "Expected two received messages.");
            Select(LocalFolderKinds.Drafts, 0);
            Select(LocalFolderKinds.Inbox, 3);
            Drag(messages, LocalFolderKinds.Drafts, false);
            Drag(messages, LocalFolderKinds.Outbox, false);
            Drag(messages, LocalFolderKinds.Inbox, false);
            var connected = services.Database.ListMessages().First(message => message.Account == "alpha-fixture");
            Drag([connected], target.Key, false);
            Drag(messages, target.Key, true);
            // Filing runs after the drop returns and writes off the window's thread, so the store is waited for.
            Pump(() => services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == target.Key) == 2,
                "Drop did not file both messages.");
            AssertSource(LocalFolderKinds.Inbox, 1);
            Select(target.Key, 2);
            Drag(CurrentMessages(), merged.Key, true);
            AssertSource(target.Key, 0);
            Select(merged.Key, 3);
            Drag(CurrentMessages(), LocalFolderKinds.Inbox, true);
            AssertSource(merged.Key, 1);
            // What is left is the imported message: it files too, adopted, and leaves the folder it was in.
            var imported = services.Database.ListMessages().Single(message => message.Source == MailContract.ArchiveSource && message.Folder == "Projects/Filed");
            Drag([imported], LocalFolderKinds.Inbox, true);
            AssertSource(merged.Key, 0);
            Pump(() => services.Database.ListMessages().Any(message => message.IsLocalStoredCopy && message.Folder == LocalFolderKinds.Inbox
                && message.Subject == "Imported Projects/Filed"), "Imported message was not adopted into the Inbox.");
            Select(LocalFolderKinds.Inbox, 4);
            if (bulkStorage)
            {
                var stored = services.Database.GetMessage(imported.Id)!.Summary;
                Check(stored.Source == "live" && stored.Folder == LocalFolderKinds.Inbox,
                    "Converted drag created a hidden original instead of moving the same occurrence.");
            }
            // A large scrolled source exercises the native viewport, not just the row count.
            for (var i = 0; i < 200; i++)
            {
                var raw = System.Text.Encoding.UTF8.GetBytes($"From: scroll@example.invalid\r\nSubject: Scroll fixture {i}\r\n\r\nScroll body\r\n");
                services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
                    "scroll-" + i, null, 0, "Scroll fixture " + i, "scroll@example.invalid", "Scroll", [],
                    DateTimeOffset.UtcNow.AddMinutes(i), raw.Length, true, 0, "Scroll body", null, bulkStorage ? services.Blobs.Put(raw) : null));
            }
            var reload = (Task)type.GetMethod("LoadMessagesAsync", flags)!.Invoke(form, null)!;
            Pump(() => reload.IsCompleted, "Scrolled source did not load.");
            reload.GetAwaiter().GetResult();
            list.EnsureVisible(list.VirtualListSize - 1);
            var bulk = services.Database.ListMessages().Where(message => message.SourceKey.StartsWith("scroll-")).ToArray();
            Drag(bulk, target.Key, true);
            AssertSource(LocalFolderKinds.Inbox, 4);
            Check(list.TopItem is { Index: >= 0 }, "Source viewport became negative/blank after a scrolled bulk drop.");
            Check(list.Items[0].Bounds.IntersectsWith(list.ClientRectangle), "Remaining source rows are outside the visible viewport.");
            Select(target.Key, 200);
            PhysicalDropToFolder(200);
            AssertSource(target.Key, 0);
            Select(LocalFolderKinds.Inbox, 204);
            Check(list.TopItem is { Index: >= 0 } && list.Items[0].Bounds.IntersectsWith(list.ClientRectangle),
                "Source stayed blank after navigating away and back.");

            var liveInboxBefore = services.Database.ListMessages().Count(m => m.Source == "live" && m.Folder == LocalFolderKinds.Inbox);
            var mixed = services.Database.CreateLocalFolder("2026-11", null, "archive", "archive-fixture|_Pinned/2026-11");
            var importedIds = new List<long>();
            for (var i = 0; i < 14; i++)
            {
                var raw = System.Text.Encoding.UTF8.GetBytes($"From: fixture@example.invalid\r\nSubject: Mixed {i}\r\n\r\nMixed {i}\r\n");
                importedIds.Add(services.Database.Upsert(new MessageInput("archive", "archive-fixture", "_Pinned/2026-11",
                    "mixed-" + i, null, 0, "Mixed " + i, "fixture@example.invalid", "Fixture", [],
                    DateTimeOffset.UnixEpoch.AddMinutes(i), raw.Length, true, 0, "Mixed " + i, null, services.Blobs.Put(raw))));
            }
            services.Database.AdoptMessages(importedIds.Take(10).ToArray(), mixed.Key);
            services.Database.MoveMessages(bulk.Take(5).Select(m => m.Id).ToArray(), mixed.Key);
            var sidebarReload = (Task)type.GetMethod("ReloadAccountsAsync", flags)!.Invoke(form, null)!;
            Pump(() => sidebarReload.IsCompleted, "Mixed folder sidebar did not reload.");
            sidebarReload.GetAwaiter().GetResult();
            Select(mixed.Key, 19);
            PhysicalDropToFolder(19);
            AssertSource(mixed.Key, 0);
            Pump(() => services.Database.ListMessages().Count(m => m.Source == "live" && m.Folder == LocalFolderKinds.Inbox) == liveInboxBefore + 14,
                "Mixed imported/adopted/received drop lost messages.");
            Select(LocalFolderKinds.Inbox, liveInboxBefore + 15);
            list.Focus();
            list.SelectedIndices.Clear();
            list.SelectedIndices.Add(10);
            list.Items[10].Focused = true;
            var deleting = (Task)type.GetMethod("DeleteMessagesAsync", flags)!.Invoke(form, [null, false])!;
            Pump(() => deleting.IsCompleted, "Inbox deletion did not finish.");
            deleting.GetAwaiter().GetResult();
            Check(list.FocusedItem?.Index == 10, "Deleting an Inbox row lost the keyboard navigation anchor.");
            SendMessage(list.Handle, 0x100, (IntPtr)0x26, IntPtr.Zero);
            SendMessage(list.Handle, 0x101, (IntPtr)0x26, IntPtr.Zero);
            Check(list.SelectedIndices.Cast<int>().SequenceEqual([9]), "Up after Inbox deletion did not select the preceding row.");
            // A separate imported mailbox has no LocalKey. It must still permit
            // dragging its messages out into the ordinary writable Inbox.
            var secondary = ImportedNode("Imported work");
            tree.SelectedNode = secondary;
            Pump(() => list.VirtualListSize == 2, "Secondary imported folder did not load.");
            var secondaryIds = services.Database.ListMessages().Where(message => message.Account == "secondary-archive-fixture").Select(message => message.Id).ToArray();
            var sibling = ImportedNode("Filed work");
            if (bulkStorage) using (var coordinator = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
            {
                coordinator.Open(); using var query = coordinator.CreateCommand();
                query.CommandText = "SELECT uuid FROM bulk_bindings WHERE source='archive' AND account='secondary-archive-fixture' AND folder='Filed work'";
                var uuid = (string)query.ExecuteScalar()!;
                using var blocker = new SqliteConnection($"Data Source={Path.Combine(root, "bound-folders", uuid, FolderBundle.FileName)};Pooling=False");
                blocker.Open(); using var transaction = blocker.BeginTransaction();
                try
                {
                    PhysicalDropToFolder(2, sibling);
                    var pulses = 0; using var heartbeat = new Forms.Timer { Interval = 20 };
                    heartbeat.Tick += (_, _) => pulses++; heartbeat.Start();
                    var clock = Stopwatch.StartNew();
                    Pump(() => clock.ElapsedMilliseconds >= 650, "Blocked archive move did not pump UI events.");
                    heartbeat.Stop();
                    Check(pulses >= 5, "Archive move blocked window events.");
                    Check(!((Task)type.GetField("_filing", flags)!.GetValue(form)!).IsCompleted, "Archive fixture did not hold the portable write.");
                    Console.WriteLine($"ARCHIVE SIBLING delayed_writer_ui_pulses={pulses}");
                }
                finally { transaction.Rollback(); }
            }
            else PhysicalDropToFolder(2, sibling);
            Pump(() => secondaryIds.All(id => services.Database.GetMessage(id)!.Summary is { Source: "archive", Account: "secondary-archive-fixture", Folder: "Filed work" }),
                "Sibling imported drop changed account/source or lost message identities.");
            if (!ReferenceEquals(sibling.TreeView, tree)) Console.WriteLine("FIXTURE reacquired sibling after background sidebar rebuild");
            sibling = ImportedNode("Filed work");
            tree.SelectedNode = sibling;
            try { Pump(() => list.VirtualListSize == 3, "Sibling destination did not display the moved messages."); }
            catch
            {
                Console.WriteLine($"SIBLING FAILURE rows={list.VirtualListSize} selected={tree.SelectedNode?.FullPath} archive={type.GetField("_selectedArchive", flags)!.GetValue(form)} filing={((Task)type.GetField("_filing", flags)!.GetValue(form)!).Status}");
                if (screenshots is not null)
                {
                    using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    image.Save(Path.Combine(screenshots, "archive-sibling-failed.png"));
                }
                throw;
            }
            var undoSibling = (Task)type.GetMethod("UndoMoveAsync", flags)!.Invoke(form, null)!;
            Pump(() => undoSibling.IsCompleted, "Sibling archive undo did not finish.");
            undoSibling.GetAwaiter().GetResult();
            Check(services.Database.ListMessages().Count(message => message.Account == "secondary-archive-fixture" && message.Folder == "Imported work") == 2,
                "Sibling archive undo did not restore both messages.");
            tree.SelectedNode = ImportedNode("Imported work");
            Pump(() => list.VirtualListSize == 2, "Undone archive source did not redraw.");
            sibling = ImportedNode("Filed work");
            PhysicalDropToFolder(2, sibling);
            sibling = ImportedNode("Filed work");
            tree.SelectedNode = sibling;
            Pump(() => list.VirtualListSize == 3, "Repeated sibling archive move did not display all messages.");
            if (bulkStorage && screenshots is not null)
            {
                var filing = (Task)type.GetField("_filing", flags)!.GetValue(form)!;
                Pump(() => filing.IsCompleted, "Sibling portable writes did not finish."); filing.GetAwaiter().GetResult();
                services.Database.FolderStorage.Flush();
                string Snapshot(string file)
                {
                    var proof = Path.Combine(root, "proof-" + Guid.NewGuid().ToString("N") + ".db");
                    using (var source = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False"))
                    using (var target = new SqliteConnection($"Data Source={proof};Pooling=False"))
                    { source.Open(); target.Open(); source.BackupDatabase(target); }
                    return Convert.ToBase64String(File.ReadAllBytes(proof));
                }
                var files = new List<string>();
                using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Mode=ReadOnly;Pooling=False"))
                {
                    c.Open(); using var q = c.CreateCommand();
                    q.CommandText = "SELECT uuid FROM bulk_bindings WHERE source='archive' AND account='secondary-archive-fixture'";
                    using var rows = q.ExecuteReader();
                    while (rows.Read()) files.Add(Path.Combine(root, "bound-folders", rows.GetString(0), FolderBundle.FileName));
                }
                File.WriteAllText(Path.Combine(screenshots, "archive-sibling-audit.json"), System.Text.Json.JsonSerializer.Serialize(new
                { expected = expectedSecondary, projection = Snapshot(services.Paths.DatabasePath), portable = files.Select(Snapshot).ToArray() }));
            }
            PhysicalDropToFolder(3);
            Pump(() => secondaryIds.All(id => services.Database.GetMessage(id)!.Summary.Folder == LocalFolderKinds.Inbox),
                "Secondary imported drag did not preserve the message references in Inbox.");
            form.Close();
            for (var pass = 0; pass < 20; pass++) { Forms.Application.DoEvents(); Thread.Sleep(10); }
            Console.WriteLine("PASS drag-events source-selection=retained source-list=refreshed drafts-outbox=same-folder=rejected connected=rejected select-all-physical-to-inbox=200 mixed-imported-adopted-received=19 delete-then-up=previous-row archive-sibling=2 undo=2 outward=3");

            Forms.TreeNode ImportedNode(string path) => Descendants(tree.Nodes).Single(node => node.Tag is { } tag
                && tag.GetType().GetProperty("Account")?.GetValue(tag) as string == "secondary-archive-fixture"
                && tag.GetType().GetProperty("ArchiveFolder")?.GetValue(tag) as string == path);

            static IEnumerable<Forms.TreeNode> Descendants(Forms.TreeNodeCollection nodes)
            {
                foreach (Forms.TreeNode node in nodes)
                {
                    yield return node;
                    foreach (var child in Descendants(node.Nodes)) yield return child;
                }
            }

            void PhysicalDropToFolder(int expected, Forms.TreeNode? destination = null)
            {
            // The guest's firewall notifications can be topmost without owning focus.
            // Keep this fixture to their right and reject an obscured input endpoint.
            var area = Forms.Screen.PrimaryScreen!.WorkingArea;
            var width = Math.Min(1000, area.Width);
            form.Bounds = new System.Drawing.Rectangle(area.Right - width, area.Top, width, Math.Min(800, area.Height));
            Forms.Application.DoEvents();
            var selectAll = (Task)type.GetMethod("SelectAllMessagesAsync", flags)!.Invoke(form, null)!;
            Pump(() => selectAll.IsCompleted, "Select All did not settle.");
            selectAll.GetAwaiter().GetResult();
            Check(list.SelectedIndices.Count == expected, "Select All omitted messages.");
            var inboxNode = destination ?? tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Inbox, true).Single();
            // Keep Inbox clear of the edge-scroll zone before measuring the gesture.
            if (destination is null) tree.TopNode = tree.Nodes[0];
            inboxNode.EnsureVisible();
            Forms.Application.DoEvents();
            list.EnsureVisible(0);
            var fromPoint = list.PointToScreen(new System.Drawing.Point(25, list.Items[0].Bounds.Top + list.Items[0].Bounds.Height / 2));
            var toPoint = tree.PointToScreen(new System.Drawing.Point(inboxNode.Bounds.Left + 10, inboxNode.Bounds.Top + inboxNode.Bounds.Height / 2));
            Check(GetAncestor(WindowFromPoint(fromPoint), 2) == form.Handle && GetAncestor(WindowFromPoint(toPoint), 2) == form.Handle,
                "Physical drag fixture endpoints are obscured by another guest window.");
            Console.WriteLine($"PHYSICAL DRAG from={fromPoint} to={toPoint} dpi={form.DeviceDpi} form={form.Handle} foreground={GetForegroundWindow()} selected={list.SelectedIndices.Count}");
            Capture("bulk-drag-before");
            // Guest-only physical input exercises ItemDrag and the OLE data boundary,
            // which invoking OnDragDrop with an in-process DataObject cannot cover.
            var gesture = Task.Run(() =>
            {
                SetCursorPos(fromPoint.X, fromPoint.Y); Thread.Sleep(100);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                try
                {
                    Thread.Sleep(100);
                    for (var step = 1; step <= 20; step++)
                    {
                        SetCursorPos(fromPoint.X + (toPoint.X - fromPoint.X) * step / 20,
                            fromPoint.Y + (toPoint.Y - fromPoint.Y) * step / 20);
                        Thread.Sleep(40);
                    }
                    Thread.Sleep(200);
                }
                finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
            });
            Pump(() => gesture.IsCompleted, "Physical drag did not finish.");
            gesture.GetAwaiter().GetResult();
            try { Pump(() => list.VirtualListSize == 0, "Select All physical drag to Inbox was rejected."); }
            catch
            {
                Console.WriteLine($"PHYSICAL DRAG failed cursor={Forms.Cursor.Position} foreground={GetForegroundWindow()} rows={list.VirtualListSize} selected={list.SelectedIndices.Count}");
                Capture("bulk-drag-failed");
                throw;
            }

            void Capture(string name)
            {
                if (screenshots is null || !bulkStorage || !Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app)) return;
                Directory.CreateDirectory(screenshots);
                var bounds = Forms.Screen.PrimaryScreen!.Bounds;
                using var image = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                using var graphics = System.Drawing.Graphics.FromImage(image);
                graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                image.Save(Path.Combine(screenshots, name + ".png"));
            }
            }

            void Pump(Func<bool> done, string failure)
            {
                var deadline = Stopwatch.StartNew();
                while (!done())
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(failure);
                    Forms.Application.DoEvents();
                    Thread.Sleep(10);
                }
            }

            MessageSummary[] CurrentMessages() => services.Database.ListMessages()
                .Where(message => message.Account == "local-fixture").ToArray();

            void Select(string key, int count)
            {
                tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + key, true).Single();
                AssertSource(key, count);
            }

            void AssertSource(string key, int count)
            {
                Check(tree.SelectedNode?.Name == "LocalFolder:" + key, "Source folder selection changed after filing: " + key);
                var deadline = Stopwatch.StartNew();
                while (list.VirtualListSize != count)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                        throw new TimeoutException($"Source list did not refresh for {key}: expected {count}, got {list.VirtualListSize}.");
                    Forms.Application.DoEvents();
                    Thread.Sleep(10);
                }
            }

            void Drag(MessageSummary[] chosen, string key, bool accepted)
            {
                var node = tree.Nodes.Find("LocalFolder:" + key, true).Single();
                node.EnsureVisible();
                var point = tree.PointToScreen(new System.Drawing.Point(node.Bounds.Left + 10, node.Bounds.Top + node.Bounds.Height / 2));
                var data = new Forms.DataObject(chosen);
                var args = new Forms.DragEventArgs(data, 1, point.X, point.Y, Forms.DragDropEffects.Move, Forms.DragDropEffects.None);
                typeof(Forms.Control).GetMethod("OnDragOver", flags)!.Invoke(tree, [args]);
                Check(args.Effect == (accepted ? Forms.DragDropEffects.Move : Forms.DragDropEffects.None), "Unexpected drop effect for " + key);
                typeof(Forms.Control).GetMethod("OnDragDrop", flags)!.Invoke(tree, [args]);
                if (!accepted)
                    Check(services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == LocalFolderKinds.Inbox) == 2,
                        "A refused drop changed the source messages.");
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
