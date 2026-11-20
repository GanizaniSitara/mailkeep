using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Real Shift+Delete and native confirmation on an isolated mailbox, including reader ownership.</summary>
internal static class PermanentDeleteSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    internal static void Run(string app, string root, MailkeepServices services)
    {
        services.Accounts.Save([]);
        services.Database.SetSetting("sync.auto", "off");
        var builder = new MimeKit.BodyBuilder { TextBody = "Harmless fixture" };
        builder.Attachments.Add("harmless.pdf", "%PDF harmless test attachment"u8.ToArray(), new MimeKit.ContentType("application", "pdf"));
        using var mime = new MimeKit.MimeMessage { Body = builder.ToMessageBody() };
        using var bytes = new MemoryStream();
        mime.WriteTo(bytes);
        var raw = services.Blobs.Put(bytes.ToArray());
        var ids = Enumerable.Range(0, 3).Select(index => services.Database.Upsert(new MessageInput(MailContract.LiveSource,
            MailContract.LocalCopyAccount, LocalFolderKinds.Inbox, "copy:purge-" + index, null, 0,
            "Permanent delete " + index, "fixture@example.invalid", "Fixture", [], DateTimeOffset.UtcNow.AddMinutes(-index),
            10, false, 1, "Harmless fixture", null, raw))).ToArray();
        Check(services.Database.GetMessage(ids[0])!.Attachments.Count == 1, "attachment fixture missing");
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
                Until(() => (bool)type.GetField("_ready", Flags)!.GetValue(main)!);
                var list = (Forms.ListView)type.GetField("_messages", Flags)!.GetValue(main)!;
                Until(() => list.VirtualListSize == 3);
                list.Focus();
                list.SelectedIndices.Add(0);
                list.SelectedIndices.Add(1);
                Confirm(() => Press(main, Forms.Keys.Shift | Forms.Keys.Delete), false);
                Check(ids.All(id => services.Database.GetMessage(id) is not null), "Cancel deleted messages");
                Confirm(() => Press(main, Forms.Keys.Shift | Forms.Keys.Delete), true);
                Until(() => services.Database.GetMessage(ids[0]) is null && services.Database.GetMessage(ids[1]) is null);
                Until(() => list.VirtualListSize == 1);
                var rows = (IReadOnlyList<object>)type.GetField("_rows", Flags)!.GetValue(main)!;
                Check(rows.Count == 1 && ((MessageSummary)rows[0]).Id == ids[2], "remaining list row did not update");
                try { services.Database.ExtractAttachment(ids[0], 0); throw new InvalidOperationException("deleted attachment remained accessible"); }
                catch (KeyNotFoundException) { }
                Check(services.Database.GetMessage(ids[2])!.Summary.Folder == LocalFolderKinds.Inbox, "unselected message changed");
                Check(services.Database.ListMessages().All(message => message.Folder != LocalFolderKinds.Deleted), "Shift+Delete moved to Deleted Items");
                using (var editor = new Forms.TextBox { Text = "editing" })
                {
                    main.Controls.Add(editor);
                    editor.Focus();
                    Check(type.GetMethod("MainShortcut", Flags)!.Invoke(main, [Forms.Keys.Shift | Forms.Keys.Delete]) is null, "Shift+Delete stole text editing");
                }
                var opening = (Task)type.GetMethod("OpenMessageAsync", Flags)!.Invoke(main, [ids[2]])!;
                Until(() => opening.IsCompleted);
                opening.GetAwaiter().GetResult();
                using var reader = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "MessageWindow");
                list.SelectedIndices.Clear();
                Confirm(() => Press(reader, Forms.Keys.Shift | Forms.Keys.Delete), true);
                Until(() => services.Database.GetMessage(ids[2]) is null);
                Console.WriteLine("PASS permanent-delete main=multi-selection cancel=retained bin=bypassed reader=own-message editing=preserved provider-writes=0");

                void Confirm(Action action, bool yes)
                {
                    var clicked = false;
                    using var timer = new Forms.Timer { Interval = 30 };
                    timer.Tick += (_, _) =>
                    {
                        var window = FindWindow("#32770", "Delete message");
                        if (window == IntPtr.Zero) return;
                        GetWindowThreadProcessId(window, out var pid);
                        if (pid != Environment.ProcessId) return;
                        clicked = true;
                        timer.Stop();
                        SendMessage(GetDlgItem(window, yes ? 6 : 7), 0xF5, IntPtr.Zero, IntPtr.Zero);
                    };
                    timer.Start();
                    action();
                    Check(clicked, "permanent delete confirmation missing");
                }
                void Until(Func<bool> condition)
                {
                    var watch = Stopwatch.StartNew();
                    while (!condition()) { Check(watch.ElapsedMilliseconds < 10000, "Permanent delete timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(45)), "Permanent delete thread timed out");
        if (failure is not null) throw new InvalidOperationException("Permanent delete failed", failure);
    }
    private static void Press(Forms.Form form, Forms.Keys keys)
    {
        var message = Forms.Message.Create(form.Handle, 0x100, (IntPtr)(int)(keys & Forms.Keys.KeyCode), IntPtr.Zero);
        Check((bool)form.GetType().GetMethod("ProcessCmdKey", Flags)!.Invoke(form, [message, keys])!, "shortcut not handled");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
