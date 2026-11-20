using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>New Inbox mail reveals the top only when idle, preserving the selected message and reader.</summary>
internal static class ArrivalScrollSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        services.Database.SetSetting("sync.auto", "off");
        services.Database.CreateArchiveFolder(MailContract.ArchiveSource, "merged-fixture", "Inbox");
        services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "merged-fixture", "Inbox", "old-import",
            null, 0, "Old imported Inbox fixture", "old@example.invalid", "Old", [], DateTimeOffset.UnixEpoch,
            128, true, 0, "Invented historical mail", null, null));
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 120; i++) Add("older-" + i, "Older message " + i.ToString("D3"), now.AddMinutes(-i));
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "arrival scroll timed out");
        if (failure is not null) throw new InvalidOperationException("arrival scroll failed", failure);
        Console.WriteLine("PASS arrival-scroll idle=top selected-message=preserved recent-native-key=preserved reading=preserved multiselect=preserved custom-sort=preserved no-arrival=no-jump");

        void Add(string key, string subject, DateTimeOffset received) => services.Database.Upsert(new MessageInput(MailContract.LiveSource,
            "local-fixture", LocalFolderKinds.Inbox, key, null, 0, subject, "sender@example.invalid", "Sender", ["local@example.invalid"],
            received, 128, true, 0, "Body for " + subject, null, null));

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
            IReadOnlyList<object> Rows() => Get<IReadOnlyList<object>>("_rows");
            MessageSummary Row(int index) => (MessageSummary)Rows()[index];
            var list = Get<Forms.ListView>("_messages");
            void Age() => type.GetField("_lastMailInput", Flags)!.SetValue(main, Environment.TickCount64 - 6_000);
            void Refresh() => Await((Task)type.GetMethod("RefreshAfterReceiveAsync", Flags)!.Invoke(main, [true])!);
            void Position()
            {
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(60); list.Items[60].Focused = true;
                list.TopItem = list.Items[55]; list.Focus();
                Until(() => Get<MessageContent?>("_currentContent")?.Summary.Id == Row(60).Id);
            }
            try
            {
                main.Show(); Until(() => Get<bool>("_ready") && list.VirtualListSize >= 120);
                Check(Get<object?>("_selectedArchive") is not null, "merged Inbox fixture did not use its imported tree representation");
                Position(); var selected = Row(list.SelectedIndices[0]).Id;
                var preview = Get<MessageContent?>("_currentContent");
                Age();
                var beforeRefreshInput = Get<long>("_mailInputVersion");
                Add("idle-arrival", "Idle arrival", now.AddMinutes(1)); Refresh();
                var settled = Stopwatch.StartNew();
                while (settled.ElapsedMilliseconds < 500) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check(Get<long>("_mailInputVersion") == beforeRefreshInput, "programmatic refresh counted as mail input");
                Check(list.TopItem?.Index == 0, "idle Inbox did not scroll to newest mail");
                Check(Row(list.SelectedIndices[0]).Id == selected && Get<MessageContent?>("_currentContent")?.Summary.Id == selected, "idle scroll replaced selected message or preview");
                Check(ReferenceEquals(preview, Get<MessageContent?>("_currentContent")), "arrival unnecessarily reloaded the selected message");
                Capture("arrival-idle-top");
                Position(); Age();
                var inputVersion = Get<long>("_mailInputVersion");
                type.GetField("_manualReceiveInputVersion", Flags)!.SetValue(main, inputVersion);
                PostMessage(list.Handle, 0x100, (IntPtr)Forms.Keys.Down, (IntPtr)1);
                Until(() => Get<long>("_mailInputVersion") != inputVersion);
                var manualTop = Row(list.TopItem!.Index).Id;
                Add("manual-working-arrival", "Manual working arrival", now.AddSeconds(90)); Refresh();
                Check(Row(list.TopItem!.Index).Id == manualTop, "manual receive interrupted work started during receive");
                type.GetField("_manualReceiveInputVersion", Flags)!.SetValue(main, null);
                Position(); Age();
                PostMessage(list.Handle, 0x100, (IntPtr)Forms.Keys.Down, (IntPtr)1);
                Until(() => Environment.TickCount64 - Get<long>("_lastMailInput") < 2000);
                var top = Row(list.TopItem!.Index).Id;
                selected = Row(list.SelectedIndices[0]).Id;
                Add("active-arrival", "Active arrival", now.AddMinutes(2)); Refresh();
                Check(Row(list.TopItem!.Index).Id == top && Row(list.SelectedIndices[0]).Id == selected, "recent keyboard work was interrupted");
                Age(); Refresh(); Check(Row(list.TopItem!.Index).Id == top, "no-arrival refresh jumped to top");
                Position(); Age(); Get<Forms.RichTextBox>("_body").Focus(); Forms.Application.DoEvents();
                var body = Get<Forms.RichTextBox>("_body"); body.Select(5, 4);
                preview = Get<MessageContent?>("_currentContent");
                top = Row(list.TopItem!.Index).Id;
                Add("reading-arrival", "Reading arrival", now.AddMinutes(3)); Refresh();
                Check(Row(list.TopItem!.Index).Id == top, "focused reader was interrupted");
                Check(ReferenceEquals(preview, Get<MessageContent?>("_currentContent")) && body.SelectionStart == 5 && body.SelectionLength == 4,
                    "arrival reset the focused reader");
                Position(); list.SelectedIndices.Add(61); Age(); top = Row(list.TopItem!.Index).Id;
                var chosen = list.SelectedIndices.Cast<int>().Select(index => Row(index).Id).Order().ToArray();
                Add("selection-arrival", "Selection arrival", now.AddMinutes(4)); Refresh();
                Check(Row(list.TopItem!.Index).Id == top && chosen.SequenceEqual(list.SelectedIndices.Cast<int>().Select(index => Row(index).Id).Order()), "multi-selection was interrupted");
                Capture("arrival-active-selection");
                type.GetField("_sortColumn", Flags)!.SetValue(main, 1);
                type.GetField("_sortDescending", Flags)!.SetValue(main, false);
                Refresh(); Position(); Age(); top = Row(list.TopItem!.Index).Id;
                Add("custom-sort-arrival", "AAAA arrival", now.AddMinutes(5)); Refresh();
                Check(Row(list.TopItem!.Index).Id == top, "custom sort was interrupted");
            }
            finally { main.Close(); Forms.Application.DoEvents(); }

            void Capture(string name)
            {
                if (screenshots is null) return;
                Directory.CreateDirectory(screenshots);
                using var image = new System.Drawing.Bitmap(main.Width, main.Height);
                main.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, image.Size));
                image.Save(Path.Combine(screenshots, name + ".png"));
            }
        }
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> condition) { var clock = Stopwatch.StartNew(); while (!condition()) { Check(clock.ElapsedMilliseconds < 15000, "arrival UI did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
