using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

/// <summary>Leaving a scrolled Inbox for Drafts — the mailbox's own drafts above the imported ones — left the
/// native list with a negative top index and nothing drawn. In process: open the Inbox, scroll it to the
/// bottom, open Drafts, then ask the list itself where it thinks it is.</summary>
internal static class DraftsListSmoke
{
    private const int LVM_GETTOPINDEX = 0x1000 + 39;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    public static void Seed(MailkeepServices services)
    {
        for (var index = 0; index < 60; index++)
            services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "archive-fixture", "Inbox", $"inbox-{index}", null, 0,
                $"Imported inbox {index}", "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow.AddMinutes(-index),
                128, true, 0, "Body", null, null));
        for (var index = 0; index < 40; index++)
            services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "archive-fixture", "Drafts", $"draft-{index}", null, 0,
                $"Imported draft {index}", "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow.AddMinutes(-index),
                128, true, 0, "Body", null, null));
        foreach (var subject in new[] { "Own draft one", "Own draft two", "Own draft three" })
            services.Send.SaveDraft("fixture-3", ["recipient@example.invalid"], subject, "Draft body");
        // Queued, never sent: the fixture's identity points nowhere and nothing here delivers the Outbox.
        foreach (var subject in new[] { "Queued one", "Queued two", "Queued three", "Queued four", "Queued five" })
            services.Send.QueueAsync(services.Send.SaveDraft("fixture-3", ["recipient@example.invalid"], subject, "Queued body").Id)
                .GetAwaiter().GetResult();
    }

    public static void Run(string app, string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
                var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var form = (System.Windows.Forms.Form)Activator.CreateInstance(type, [root])!;
                form.Show();
                Pump(() => (bool)type.GetField("_ready", flags)!.GetValue(form)!, "window ready");
                var tree = (System.Windows.Forms.TreeView)type.GetField("_localTree", flags)!.GetValue(form)!;
                var list = (System.Windows.Forms.ListView)type.GetField("_messages", flags)!.GetValue(form)!;

                // Every folder, entered from a list scrolled to the bottom: the fault is in how a list is
                // refilled, so no folder gets a pass for being the one that was reported.
                var failures = new List<string>();
                foreach (var target in new[] { LocalFolderKinds.Drafts, LocalFolderKinds.Outbox, LocalFolderKinds.Sent,
                             LocalFolderKinds.Deleted, LocalFolderKinds.Inbox })
                {
                    tree.SelectedNode = Node(tree, LocalFolderKinds.Inbox == target ? LocalFolderKinds.Drafts : LocalFolderKinds.Inbox);
                    Pump(() => list.VirtualListSize >= 40, "a long folder to scroll");
                    Pump(() => false, "settle", TimeSpan.FromMilliseconds(300));
                    list.EnsureVisible(list.VirtualListSize - 1);
                    Pump(() => false, "settle", TimeSpan.FromMilliseconds(200));

                    tree.SelectedNode = Node(tree, target);
                    Pump(() => false, "settle", TimeSpan.FromMilliseconds(600));
                    var top = (int)SendMessage(list.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
                    var rowTop = list.VirtualListSize > 0 ? list.GetItemRect(0).Top : 0;
                    Console.WriteLine($"{target}: rows={list.VirtualListSize} top_index={top} row0_top={rowTop}");
                    if (top != 0 || rowTop < 0 || rowTop > 60) failures.Add($"{target} top_index={top} row0_top={rowTop}");
                }
                if (failures.Count > 0) throw new InvalidOperationException("rows drawn off screen: " + string.Join("; ", failures));
                Console.WriteLine("PASS drafts-list (every standard folder)");
                form.Dispose();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("drafts-list did not finish");
        if (failure is not null) throw failure;
    }

    private static System.Windows.Forms.TreeNode Node(System.Windows.Forms.TreeView tree, string key)
    {
        static IEnumerable<System.Windows.Forms.TreeNode> Walk(System.Windows.Forms.TreeNodeCollection nodes)
        {
            foreach (System.Windows.Forms.TreeNode node in nodes)
            {
                yield return node;
                foreach (var child in Walk(node.Nodes)) yield return child;
            }
        }
        return Walk(tree.Nodes).FirstOrDefault(node => node.Name == "LocalFolder:" + key)
            ?? throw new InvalidOperationException($"no tree node for {key}");
    }

    private static void Pump(Func<bool> done, string what, TimeSpan? wait = null)
    {
        var limit = wait ?? TimeSpan.FromSeconds(15);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (done()) return;
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(10);
        }
        if (wait is null) throw new TimeoutException($"timed out waiting for {what}");
    }
}
