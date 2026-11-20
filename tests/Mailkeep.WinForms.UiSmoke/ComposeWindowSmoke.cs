using System.Diagnostics;
using System.Reflection;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Independent compose windows, minimize/exit protection and draft commits; isolated mail, no sending.</summary>
internal static class ComposeWindowSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(string app, string root, MailkeepServices services, string? screenshots, bool abort = false)
    {
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(45)), "compose-window test timed out");
        if (abort)
        {
            Check(failure is InvalidOperationException { Message: "Deliberate fixture failure" }, "fixture cleanup hid the original failure: " + failure);
            Console.WriteLine("PASS compose-window-abort original-failure=preserved open-composer=closed STA-cleanup=no-WebView-exception");
            return;
        }
        if (failure is not null) throw new InvalidOperationException("compose-window test failed", failure);
        Console.WriteLine("PASS compose-window main=enabled/minimizable compose=independent exit=protected failed-commit=retained draft=saved cancel=no-write Alt+S=queued-only/no-delivery");

        void RunUi()
        {
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetDefaultFont(new System.Drawing.Font("Tahoma", 8.25f));
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            System.Runtime.InteropServices.NativeLibrary.Load(loader);
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
            var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            var seedType = assembly.GetType("Mailkeep.WinForms.ComposeSeed", true)!;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            try
            {
            main.Show(); Until(() => (bool)type.GetField("_ready", Flags)!.GetValue(main)!);
            var seed = Activator.CreateInstance(seedType, ["New Message", "fixture-3", "recipient@example.invalid", "", "", "Independent draft", "Unsaved fixture words", null, null, null, null, null])!;
            var open = type.GetMethod("ComposeAsync", Flags)!;
            using var watchdog = new Forms.Timer { Interval = 50 };
            Exception? modalFailure = null;
            watchdog.Tick += (_, _) =>
            {
                var modal = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(form => form.GetType().Name == "ComposeForm" && form.Modal);
                if (modal is null) return;
                modalFailure = new InvalidOperationException("compose disables the main window as a modal dialog");
                modal.GetType().GetField("_discard", Flags)!.SetValue(modal, true);
                modal.DialogResult = Forms.DialogResult.Cancel;
            };
            watchdog.Start();
            Await((Task)open.Invoke(main, [seed])!);
            watchdog.Stop(); if (modalFailure is not null) throw modalFailure;
            var compose = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "ComposeForm");
            var composeType = compose.GetType();
            WaitForBody(compose, "Unsaved fixture words");
            Console.WriteLine("COMPOSE WINDOW body ready: HTML=" + !Plain(compose).Visible);
            if (abort) throw new InvalidOperationException("Deliberate fixture failure");
            if (screenshots is not null)
            {
                Directory.CreateDirectory(screenshots);
                foreach (var (name, bounds) in new[] {
                    ("landscape", new System.Drawing.Rectangle(0, 0, 1024, 760)),
                    ("portrait-secondary", new System.Drawing.Rectangle(1920, -386, 620, 800)) })
                {
                    SetWindowPos(compose.Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x14);
                    Forms.Application.DoEvents();
                    using var bitmap = new System.Drawing.Bitmap(compose.Width, compose.Height);
                    compose.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, compose.Size));
                    bitmap.Save(Path.Combine(screenshots, "compose-address-" + name + ".png"));
                    foreach (var fieldName in new[] { "_to", "_cc", "_bcc", "_subject" })
                    {
                        var field = (Forms.TextBox)composeType.GetField(fieldName, Flags)!.GetValue(compose)!;
                        var layout = (Forms.TableLayoutPanel)field.Parent!;
                        var row = layout.GetRow(field);
                        var heights = layout.GetRowHeights();
                        var top = layout.Padding.Top + heights.Take(row).Sum();
                        Check(Math.Abs(field.Top - top - (heights[row] - field.Height) / 2) <= 1, "field is not vertically centered: " + fieldName);
                        if (fieldName == "_to")
                        {
                            var origin = field.PointToScreen(System.Drawing.Point.Empty);
                            var ink = new List<int>();
                            for (var y = 1; y < field.ClientSize.Height - 1; y++)
                                for (var x = 2; x < field.ClientSize.Width - 2; x++)
                                {
                                    var color = bitmap.GetPixel(origin.X - compose.Left + x, origin.Y - compose.Top + y);
                                    if (color.R < 100 && color.G < 100 && color.B < 100) { ink.Add(y); break; }
                                }
                            // Native edits place text by the font baseline, not by the visible ink's
                            // centre (which depends on the letters). Check row centring above and
                            // unclipped ink here rather than demanding symmetric glyph whitespace.
                            Check(ink.Count > 0 && ink.Min() > 0 && ink.Max() < field.ClientSize.Height - 2,
                                "address text is missing or clipped");
                        }
                    }
                }
            }
            Check(main.Enabled && !compose.Modal && compose.Owner is null, "compose still restricts its main window");
            main.WindowState = Forms.FormWindowState.Minimized; Forms.Application.DoEvents();
            Check(main.WindowState == Forms.FormWindowState.Minimized && compose.Visible && compose.WindowState != Forms.FormWindowState.Minimized,
                "minimizing main hides compose");
            WriteBody(compose, "Draft remains editable while main is minimized.");
            main.Close(); Forms.Application.DoEvents();
            Check(!main.IsDisposed && !compose.IsDisposed && ReadBody(compose).StartsWith("Draft remains"), "closing main loses open compose");
            var commit = composeType.GetProperty("CommitMessage", Flags)!;
            var originalCommit = commit.GetValue(compose);
            commit.SetValue(compose, (Func<Task<bool>>)(() => Task.FromResult(false)));
            var finish = composeType.GetMethod("FinishAsync", Flags)!;
            Await((Task)finish.Invoke(compose, [false])!);
            Check(!compose.IsDisposed && compose.Enabled && ReadBody(compose).StartsWith("Draft remains"), "failed commit closed or disabled the editor");
            commit.SetValue(compose, originalCommit);
            Await((Task)finish.Invoke(compose, [false])!);
            Check(compose.IsDisposed, "successful draft commit did not close modeless compose");
            var drafts = services.Database.ListOutgoing("draft", 100);
            Check(drafts.Count == 1 && drafts[0].BodyText == "Draft remains editable while main is minimized.", "draft body missing or duplicated");
            main.WindowState = Forms.FormWindowState.Normal;
            Await((Task)open.Invoke(main, [seed])!);
            var cancelled = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "ComposeForm");
            cancelled.GetType().GetField("_discard", Flags)!.SetValue(cancelled, true);
            cancelled.Close(); Forms.Application.DoEvents();
            Check(services.Database.ListOutgoing("draft", 100).Count == 1 && services.Database.ListOutgoing("outbox", 100).Count == 0,
                "cancel saved or queued fixture mail");
            services.Database.SetSetting("send.immediately", "false");
            Await((Task)open.Invoke(main, [seed])!);
            var sending = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "ComposeForm");
            WaitForBody(sending, "Unsaved fixture words");
            var keys = Forms.Keys.Alt | Forms.Keys.S;
            var message = Forms.Message.Create(sending.Handle, 0x100, (IntPtr)(int)Forms.Keys.S, IntPtr.Zero);
            Check((bool)sending.GetType().GetMethod("ProcessCmdKey", Flags)!.Invoke(sending, [message, keys])!, "modeless Alt+S was not handled");
            Until(() => sending.IsDisposed);
            var queued = services.Database.ListOutgoing("outbox", 100);
            Check(queued.Count == 1 && queued[0].AttemptCount == 0 && services.Database.ListOutgoing("sent", 100).Count == 0,
                "modeless Send ignored queue-only setting or attempted delivery");
            main.Close(); Check(main.IsDisposed, "main cannot exit after compose windows close");
            }
            finally
            {
                // A failed assertion must close modeless windows while their owning STA is alive.
                // Disposing only main leaves WebView COM wrappers alive after the thread exits,
                // replacing the useful assertion with a later OnGotFocus/MoveFocus exception.
                foreach (var window in Forms.Application.OpenForms.Cast<Forms.Form>().Where(form => form.GetType().Name == "ComposeForm").ToArray())
                {
                    window.GetType().GetField("_discard", Flags)!.SetValue(window, true);
                    window.Close();
                }
                if (!main.IsDisposed) main.Close();
                Forms.Application.DoEvents();
            }
        }
    }
    private static Forms.TextBox Plain(Forms.Form compose) => (Forms.TextBox)compose.GetType().GetField("_plain", Flags)!.GetValue(compose)!;
    private static object Editor(Forms.Form compose) => compose.GetType().GetField("_editor", Flags)!.GetValue(compose)!;
    private static void WaitForBody(Forms.Form compose, string expected)
    {
        Task<string>? pending = null;
        Until(() =>
        {
            if (Plain(compose).Visible) return Plain(compose).Text.Contains(expected);
            var editor = Editor(compose);
            if (!(bool)editor.GetType().GetField("_ready", Flags)!.GetValue(editor)!) return false;
            pending ??= (Task<string>)editor.GetType().GetMethod("GetTextAsync")!.Invoke(editor, null)!;
            if (!pending.IsCompleted) return false;
            var text = pending.GetAwaiter().GetResult(); pending = null;
            return text.Contains(expected);
        });
    }
    private static string ReadBody(Forms.Form compose)
    {
        if (Plain(compose).Visible) return Plain(compose).Text;
        var editor = Editor(compose);
        var task = (Task<string>)editor.GetType().GetMethod("GetTextAsync")!.Invoke(editor, null)!;
        Await(task); return task.GetAwaiter().GetResult();
    }
    private static void WriteBody(Forms.Form compose, string text)
    {
        if (Plain(compose).Visible) { Plain(compose).Text = text; return; }
        var editor = Editor(compose);
        var core = editor.GetType().GetProperty("CoreWebView2")!.GetValue(editor)!;
        Await((Task)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core,
            ["document.body.innerText=" + System.Text.Json.JsonSerializer.Serialize(text)])!);
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Check(clock.ElapsedMilliseconds < 12000, "compose fixture did not settle");
            Forms.Application.DoEvents(); Thread.Sleep(10);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
