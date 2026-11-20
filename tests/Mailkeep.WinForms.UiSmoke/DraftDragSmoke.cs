using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Real drag/drop handlers must file saved drafts and keep compose and attachments available.</summary>
internal static class DraftDragSmoke
{
    internal static void Run(string app, string root, MailkeepServices services, string? screenshots, bool physical = false, bool converted = false)
    {
        var folder = services.Database.CreateLocalFolder("My Stuff", null);
        var attachment = Path.Combine(root, "retained-draft.txt");
        File.WriteAllText(attachment, "retained draft attachment");
        var draft = services.Send.SaveDraft("fixture-3", ["recipient@example.invalid"], "Filed editable draft", "Unsent retained body",
            attachmentPaths: [attachment], bodyHtml: "<p>Unsent retained body</p>");
        if (converted)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
            c.Open();
            object[] Read(string table)
            {
                using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM " + table;
                using var rows = q.ExecuteReader();
                var values = new List<object>();
                while (rows.Read()) values.Add(Enumerable.Range(0, rows.FieldCount).ToDictionary(rows.GetName, i => rows.IsDBNull(i) ? null : rows.GetValue(i)));
                return values.ToArray();
            }
            var hierarchy = System.Text.Json.JsonSerializer.Serialize(new { local_folders = Read("local_folders"), archive_folders = Read("archive_folders") });
            var uuid = Guid.NewGuid().ToString("N");
            FolderBundle.WriteBulk(Path.Combine(root, "bound-folders", uuid, FolderBundle.FileName), uuid,
                new("live", "", folder.Key, hierarchy, Logical: true), []);
            services.Database.FolderStorage.RebuildProjection();
        }
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
                System.Runtime.InteropServices.NativeLibrary.Load(loader);
                var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                main.Show();
                Until(() => (bool)type.GetField("_ready", flags)!.GetValue(main)!);
                Forms.Form? find = null;
                if (converted)
                {
                    find = (Forms.Form)type.GetMethod("FindForm", flags)!.Invoke(main, [])!;
                    find.Show();
                }
                var tree = (Forms.TreeView)type.GetField("_localTree", flags)!.GetValue(main)!;
                var list = (Forms.ListView)type.GetField("_messages", flags)!.GetValue(main)!;
                Forms.TreeNode Node(string key) => Walk(tree.Nodes).Single(node => node.Name == "LocalFolder:" + key);
                tree.SelectedNode = Node(LocalFolderKinds.Drafts);
                Until(() => list.VirtualListSize == 1 && ((IEnumerable<object>)type.GetField("_rows", flags)!.GetValue(main)!)
                    .SingleOrDefault() is OutgoingMessage { Status: OutgoingStates.Draft });
                list.Items[0].Selected = true;
                Until(() => ((OutgoingMessage[])type.GetMethod("SelectedDrafts", flags)!.Invoke(main, null)!).Length == 1);
                if (physical)
                {
                    Console.WriteLine("READY physical draft drag: selected Drafts row to My Stuff on guest desktop");
                    Until(() => services.Database.DraftFolder(draft.Id) == folder.Key);
                }
                else Drop(folder.Key, true);
                Until(() => services.Database.DraftFolder(draft.Id) == folder.Key && list.VirtualListSize == 0);
                tree.SelectedNode = Node(folder.Key);
                Until(() => list.VirtualListSize == 1);
                list.Items[0].Selected = true;
                Until(() => ((Forms.Label)type.GetField("_subject", flags)!.GetValue(main)!).Text == draft.Subject);
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(main.Width, main.Height);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(main.Location, System.Drawing.Point.Empty, bitmap.Size);
                    bitmap.Save(Path.Combine(screenshots, "draft-in-my-stuff.png"));
                }
                _ = (Task)type.GetMethod("OpenSelectedMessageAsync", flags)!.Invoke(main, null)!;
                Until(() => Forms.Application.OpenForms.Cast<Forms.Form>().Any(form => form.GetType().Name == "ComposeForm"));
                var compose = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "ComposeForm");
                var subject = (Forms.TextBox)compose.GetType().GetField("_subject", flags)!.GetValue(compose)!;
                var attachments = (Forms.ListView)compose.GetType().GetField("_attachments", flags)!.GetValue(compose)!;
                Check(subject.Text == draft.Subject && !subject.ReadOnly && attachments.Items.Count == 1, "filed draft lost editability or attachment");
                compose.Dispose();
                Drop(LocalFolderKinds.Drafts, true);
                Until(() => services.Database.DraftFolder(draft.Id) == LocalFolderKinds.Drafts && list.VirtualListSize == 0);
                tree.SelectedNode = Node(LocalFolderKinds.Drafts);
                Until(() => list.VirtualListSize == 1);
                list.Items[0].Selected = true;
                Drop(LocalFolderKinds.Outbox, false);
                Check(services.Database.GetOutgoing(draft.Id)!.Status == OutgoingStates.Draft
                    && services.Database.ClaimNextOutgoing() is null, "dragging queued or sent draft mail");
                find?.Close();
                Console.WriteLine("PASS draft-drag Drafts->MyStuff->Drafts editable=yes attachment=retained Outbox=refused sends=0 physical=" + physical + " converted=" + converted);

                void Drop(string key, bool accepted)
                {
                    var node = Node(key); node.EnsureVisible();
                    var point = tree.PointToScreen(new System.Drawing.Point(node.Bounds.Left + 8, node.Bounds.Top + node.Bounds.Height / 2));
                    var data = new Forms.DataObject(); data.SetData(typeof(OutgoingMessage[]), new[] { services.Database.GetOutgoing(draft.Id)! });
                    var args = new Forms.DragEventArgs(data, 1, point.X, point.Y, Forms.DragDropEffects.Move, Forms.DragDropEffects.None);
                    typeof(Forms.Control).GetMethod("OnDragOver", flags)!.Invoke(tree, [args]);
                    Check(args.Effect == (accepted ? Forms.DragDropEffects.Move : Forms.DragDropEffects.None), "unexpected draft drop effect for " + key);
                    typeof(Forms.Control).GetMethod("OnDragDrop", flags)!.Invoke(tree, [args]);
                    Forms.Application.DoEvents();
                }
                void Until(Func<bool> condition)
                {
                    var clock = Stopwatch.StartNew();
                    while (!condition()) { Check(clock.ElapsedMilliseconds < (physical ? 120000 : 20000), "draft drag fixture timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(physical ? 180 : 75)), "draft drag fixture timed out");
        if (failure is not null) throw new InvalidOperationException("Draft drag UI failed", failure);
    }
    private static IEnumerable<Forms.TreeNode> Walk(Forms.TreeNodeCollection nodes)
    {
        foreach (Forms.TreeNode node in nodes) { yield return node; foreach (var child in Walk(node.Nodes)) yield return child; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
