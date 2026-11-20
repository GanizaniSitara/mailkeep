using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Guest-only mouse hover, without invoking any toolbar command or transport.</summary>
internal static class ToolbarHoverSmoke
{
    public static void Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        Check(Path.GetFullPath(app).StartsWith(@"C:\mk-smoke\app\", StringComparison.OrdinalIgnoreCase), "hover scenario requires isolated guest bundle");
        services.Database.SetSetting("sync.auto", "off");
        var outgoingBefore = System.Text.Json.JsonSerializer.Serialize(services.Database.ListOutgoing());
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(60)), "hover test timed out");
        if (failure is not null) throw new InvalidOperationException("toolbar hover failed", failure);

        void RunUi()
        {
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetDefaultFont(new Font("Tahoma", 8.25f));
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var type = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Mailkeep.WinForms.dll"))
                .GetType("Mailkeep.WinForms.MainForm", true)!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            main.Show();
            Pump(() => (bool)type.GetField("_ready", flags)!.GetValue(main)!);
            var toolbar = (Forms.ToolStrip)type.GetField("_mainToolbar", flags)!.GetValue(main)!;
            var receive = (Forms.ToolStripSplitButton)type.GetField("_receive", flags)!.GetValue(main)!;
            var reports = new List<object>();
            var complete = true;
            foreach (var width in new[] { 1280, 1100 })
            {
                main.WindowState = Forms.FormWindowState.Normal;
                main.Bounds = new Rectangle(0, 0, width, 850);
                SetCursorPos(50, 800); Pause();
                foreach (var arrow in new[] { false, true })
                {
                    var bounds = receive.Bounds;
                    Check(receive.Placement == Forms.ToolStripItemPlacement.Main, "split button overflowed");
                    var hover = toolbar.PointToScreen(new Point(arrow ? bounds.Right - 5 : bounds.Left + 10, bounds.Top + bounds.Height / 2));
                    SetCursorPos(hover.X, hover.Y); Pause();
                    Check(receive.Selected && !receive.DropDown.Visible, "mouse did not hover the closed split button");
                    var origin = toolbar.PointToScreen(new Point(bounds.Left - 4, bounds.Top - 4));
                    using var image = new Bitmap(bounds.Width + 8, bounds.Height + 8);
                    using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(origin, Point.Empty, image.Size);
                    var frame = image.GetPixel(4 + bounds.Width / 2, 4).ToArgb();
                    Check(frame != image.GetPixel(4 + bounds.Width / 2, 7).ToArgb(), "frame is indistinguishable from hover fill");
                    var sides = new[] { Count(true, 0), Count(true, bounds.Height - 1), Count(false, 0), Count(false, bounds.Width - 1) };
                    var pass = sides[0] >= bounds.Width / 2 && sides[1] >= bounds.Width / 2
                        && sides[2] >= bounds.Height / 2 && sides[3] >= bounds.Height / 2;
                    complete &= pass;
                    var name = $"hover-{width}-{(arrow ? "arrow" : "button")}";
                    if (screenshots is not null)
                    {
                        Directory.CreateDirectory(screenshots);
                        image.Save(Path.Combine(screenshots, name + ".png"));
                        using var full = new Bitmap(main.Width, main.Height);
                        using (var graphics = Graphics.FromImage(full)) graphics.CopyFromScreen(main.Location, Point.Empty, main.Size);
                        full.Save(Path.Combine(screenshots, name + "-window.png"));
                    }
                    Console.WriteLine($"{name}: dpi={main.DeviceDpi} renderer={toolbar.Renderer.GetType().Name} toolbar={toolbar.ClientRectangle} item={bounds} margin={receive.Margin} top/bottom/left/right={string.Join('/', sides)} complete={pass}");
                    reports.Add(new { name, dpi = main.DeviceDpi, toolbar = toolbar.ClientRectangle.ToString(), item = bounds.ToString(),
                        items = toolbar.Items.Cast<Forms.ToolStripItem>().Select(item => new { item.Text, bounds = item.Bounds.ToString() }).ToArray(),
                        frame = Color.FromArgb(frame).ToString(), sides, pass });

                    int Count(bool horizontal, int start)
                    {
                        var length = horizontal ? bounds.Width : bounds.Height;
                        return Enumerable.Range(2, length - 4).Count(along =>
                            image.GetPixel(4 + (horizontal ? along : start), 4 + (horizontal ? start : along)).ToArgb() == frame);
                    }
                }
            }
            Check(services.Database.RecentFetches().Count == 0, "hover invoked collection");
            Check(System.Text.Json.JsonSerializer.Serialize(services.Database.ListOutgoing()) == outgoingBefore, "hover changed outgoing mail");
            main.Close();
            if (screenshots is not null) File.WriteAllText(Path.Combine(screenshots, "hover-evidence.json"),
                System.Text.Json.JsonSerializer.Serialize(new { reports, fetchAttempts = services.Database.RecentFetches().Count, outgoingUnchanged = true }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Check(complete, "one or more hover border sides are incomplete");
            Console.WriteLine("PASS toolbar-hover all four blue borders; button and arrow hover at 1280/1100; no clicks, no collection");
        }
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private static void Pause() { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < 250) { Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Pump(Func<bool> done) { var clock = Stopwatch.StartNew(); while (!done()) { Check(clock.ElapsedMilliseconds < 15000, "UI did not settle"); Pause(); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
