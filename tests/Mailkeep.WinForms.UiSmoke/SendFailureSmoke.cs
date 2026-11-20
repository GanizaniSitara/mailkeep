using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Real send-error MessageBox and diagnostic log, invented failures only; no network operations.</summary>
internal static class SendFailureSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static int Run(string app, string? screenshots)
    {
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-send-error-" + Guid.NewGuid().ToString("N"));
        var services = new MailkeepServices(root);
        services.Database.Initialize();
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(60)), "send-error fixture timed out");
        if (failure is not null) throw new InvalidOperationException("send-error fixture failed", failure);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // WebView's browser process may still be releasing its transient cache.
            Console.WriteLine("Retained transient fixture cache for guest cleanup: " + root);
        }
        Console.WriteLine("PASS send-error real-dialog singular/plural=clear raw=log-only log-failure=honest network=none layouts=landscape/portrait-secondary-simulated");
        return 0;

        void RunUi()
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetDefaultFont(new System.Drawing.Font("Tahoma", 8.25f));
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            NativeLibrary.Load(Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll"));
            var type = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            try
            {
                main.Show(); Until(() => (bool)type.GetField("_ready", Flags)!.GetValue(main)!);
                var show = type.GetMethod("ShowSendFailureAsync", Flags) ?? throw new InvalidOperationException("Missing concise send-error path");
                const string raw = "An existing connection was forcibly closed by the remote host.\nSSL/TLS certificate checklist";
                foreach (var (name, count, blocked) in new[] { ("landscape", 5, false), ("portrait-secondary", 1, false), ("log-unavailable", 1, true) })
                {
                    main.Bounds = name == "portrait-secondary" ? new(1920, -300, 620, 800) : new(20, 20, 1000, 700);
                    if (blocked) { File.Delete(Path.Combine(root, "errors.log")); Directory.CreateDirectory(Path.Combine(root, "errors.log")); }
                    Exception? dialogError = null;
                    var captured = false;
                    var visibleTicks = 0;
                    using var timer = new Forms.Timer { Interval = 100 };
                    timer.Tick += (_, _) =>
                    {
                        var dialog = FindWindow(null, "Could not send mail");
                        if (dialog == IntPtr.Zero) return;
                        if (++visibleTicks < 3) return; // Let native controls paint before capturing.
                        if (captured || dialogError is not null)
                        {
                            // A native MessageBox can pump timers while still initializing.
                            // Close on a later tick, once it has entered its modal loop.
                            PostMessage(dialog, 0x10, IntPtr.Zero, IntPtr.Zero);
                            return;
                        }
                        try
                        {
                            var text = new StringBuilder();
                            EnumChildWindows(dialog, (child, _) => { var value = new StringBuilder(4096); GetWindowText(child, value, value.Capacity); text.AppendLine(value.ToString()); return true; }, IntPtr.Zero);
                            var message = text.ToString();
                            Check(message.Contains(count == 1 ? "1 message could not be sent" : "5 messages could not be sent"), "wrong failure count");
                            Check(message.Contains("still in Outbox") && message.Contains("Send/Receive"), "missing retention/retry guidance");
                            Check(!message.Contains("SSL") && !message.Contains("forcibly") && !message.Contains("github.com"), "raw library detail leaked into dialog");
                            Check(message.Contains(blocked ? "could not be written" : "have been saved"), "log result misrepresented");
                            if (screenshots is not null)
                            {
                                Directory.CreateDirectory(screenshots);
                                GetWindowRect(dialog, out var rect);
                                using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
                                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                                var dc = graphics.GetHdc();
                                try { Check(PrintWindow(dialog, dc, 2), "dialog capture failed"); }
                                finally { graphics.ReleaseHdc(dc); }
                                bitmap.Save(Path.Combine(screenshots, "send-error-" + name + ".png"));
                            }
                            captured = true;
                        }
                        catch (Exception error) { dialogError = error; }
                    };
                    timer.Start();
                    var results = Enumerable.Range(0, count).Select(i => new SendResult("fake-" + i, false, raw)).ToArray();
                    var pending = (Task)show.Invoke(main, [results])!;
                    Until(() => pending.IsCompleted);
                    pending.GetAwaiter().GetResult();
                    if (dialogError is not null) throw dialogError;
                    Check(captured, "error dialog not captured");
                    if (!blocked)
                    {
                        var log = File.ReadAllText(Path.Combine(root, "errors.log"));
                        Check(log.Contains("fake-0: " + raw), "technical details missing from log");
                    }
                }
            }
            finally { main.Close(); Forms.Application.DoEvents(); }
        }
    }
    private static void Until(Func<bool> done) { var clock = Stopwatch.StartNew(); while (!done()) { Check(clock.ElapsedMilliseconds < 20000, "send-error UI did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private delegate bool EnumProc(IntPtr window, IntPtr param);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
