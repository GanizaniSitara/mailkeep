using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>The actual Outbox editing workflow with current HTML and attachment controls; queue only.</summary>
internal static class OutboxEditSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(string app, string root, MailkeepServices services)
    {
        services.Database.SetSetting("sync.auto", "off");
        services.Database.SetSetting("send.immediately", "false");
        var queued = services.Database.ListOutgoing(OutgoingStates.Outbox).Single();
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(75)), "Outbox editing timed out");
        if (failure is not null) throw new InvalidOperationException("Outbox editing failed", failure);
        Console.WriteLine("PASS outbox-edit decline=unchanged return=draft cancel=stays-draft requeue=HTML-edited attachment=byte-identical no-send-attempt");

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
            T Get<T>(string name) => (T)type.GetField(name, Flags)!.GetValue(main)!;
            main.Show(); Until(() => Get<bool>("_ready"));
            var tree = Get<Forms.TreeView>("_localTree");
            tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Outbox, true).Single();
            Until(() => Get<OutgoingMessage?>("_currentOutgoing")?.Id == queued.Id);
            var opening = type.GetMethod("OpenSelectedMessageAsync", Flags)!;
            Task? pending = null;
            Answer("Edit queued message", 7, () => pending = (Task)opening.Invoke(main, [])!);
            Await(pending!);
            Check(services.Database.GetOutgoing(queued.Id)!.Status == OutgoingStates.Outbox, "declining changed queue state");
            Answer("Edit queued message", 6, () => pending = (Task)opening.Invoke(main, [])!);
            Await(pending!);
            var compose = Composer(); var composeType = compose.GetType();
            Check(services.Database.GetOutgoing(queued.Id)!.Status == OutgoingStates.Draft, "editing left the message queued");
            var attachments = (Forms.ListView)composeType.GetField("_attachments", Flags)!.GetValue(compose)!;
            Check(attachments.Items.Count == 1, "editing lost the attachment");
            Answer(compose.Text, 7, compose.Close);
            Until(() => compose.IsDisposed);
            Check(services.Database.GetOutgoing(queued.Id)!.Status == OutgoingStates.Draft, "cancel requeued the draft");
            tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Drafts, true).Single();
            Until(() => Get<OutgoingMessage?>("_currentOutgoing")?.Id == queued.Id);
            Await((Task)opening.Invoke(main, [])!);
            compose = Composer(); composeType = compose.GetType();
            var editor = composeType.GetField("_editor", Flags)!.GetValue(compose)!;
            Until(() => (bool)editor.GetType().GetField("_ready", Flags)!.GetValue(editor)!);
            // Wait for the original document before editing, so a late initial navigation cannot replace typing.
            Task<string>? body = null;
            Until(() =>
            {
                body ??= (Task<string>)editor.GetType().GetMethod("GetHtmlAsync")!.Invoke(editor, [])!;
                if (!body.IsCompleted) return false;
                if (body.GetAwaiter().GetResult().Contains("Queued body")) return true;
                body = null; return false;
            });
            ((Forms.TextBox)composeType.GetField("_subject", Flags)!.GetValue(compose)!).Text = "Requeued edited fixture";
            var core = editor.GetType().GetProperty("CoreWebView2")!.GetValue(editor)!;
            Await((Task)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core, ["document.body.innerText='Compose body fixture'"])!);
            Await((Task)composeType.GetMethod("FinishAsync", Flags)!.Invoke(compose, [true])!);
            Until(() => compose.IsDisposed);
            var result = services.Database.GetOutgoing(queued.Id)!;
            Check(result.Status == OutgoingStates.Outbox && result.Subject == "Requeued edited fixture" && result.BodyText.Trim() == "Compose body fixture", "requeue lost edits");
            Check(result.AttemptCount == 0 && services.Database.ListOutgoing().Count == 1, "requeue duplicated or attempted delivery");
            Check(File.ReadAllText(services.Database.ExtractOutgoingAttachment(queued.Id, 0)) == "Queued attachment bytes", "requeue changed attachment bytes");
            main.Close();
        }
    }
    private static Forms.Form Composer() => Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "ComposeForm");
    private static void Answer(string title, int answer, Action action)
    {
        var observed = false;
        using var timer = new Forms.Timer { Interval = 25 };
        timer.Tick += (_, _) =>
        {
            var window = FindWindow("#32770", title);
            if (window == IntPtr.Zero) return;
            GetWindowThreadProcessId(window, out var pid); if (pid != Environment.ProcessId) return;
            observed = true; timer.Stop();
            SendMessage(window, 0x111, (IntPtr)answer, GetDlgItem(window, answer));
        };
        timer.Start(); action(); Until(() => observed);
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> done) { var clock = Stopwatch.StartNew(); while (!done()) { Check(clock.ElapsedMilliseconds < 15000, "Outbox UI did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string type, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
