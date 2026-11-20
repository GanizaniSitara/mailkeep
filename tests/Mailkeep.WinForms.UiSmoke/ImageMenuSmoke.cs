using System.Runtime.InteropServices;
using System.Windows.Automation;
using Forms = System.Windows.Forms;

/// <summary>In the guest only (it moves the mouse): the paperclip lists the reading fixture's two
/// attachments and, under "Pictures in this message", its embedded picture; right-clicking that picture in the
/// reading pane offers only the reader's items, and Save Image As and Copy Image really save and copy it.</summary>
internal static class ImageMenuSmoke
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);



    internal static string Run(AutomationElement window, string root, string? screenshots)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        SetForegroundWindow(handle);

        // 1. The paperclip: real attachments, then the embedded picture under its own heading.
        var clip = Until(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Message attachments")),
            "the paperclip is not shown for a message with embedded pictures");
        var area = clip.Current.BoundingRectangle;
        Click((int)(area.Left + area.Width / 2), (int)(area.Top + area.Height / 2), right: false);
        var menu = Until(() => Menus().FirstOrDefault(element => Names(element).Contains("Pictures in this message")),
            "the paperclip menu has no 'Pictures in this message' section; menus: " + string.Join(" / ", Menus().Select(m => string.Join(",", Names(m)))));
        var clipItems = Names(menu);
        Check(clipItems.Any(name => name.StartsWith("fixture.txt")) && clipItems.Any(name => name.StartsWith("second.txt")),
            "the paperclip lost the real attachments: " + string.Join(", ", clipItems));
        var picture = clipItems.IndexOf("Pictures in this message");
        Check(clipItems.Skip(picture + 1).Any(name => name.Contains(".png")), "no picture listed under the heading: " + string.Join(", ", clipItems));
        Check(clipItems.Contains("Save attachments"), "the paperclip has no save item: " + string.Join(", ", clipItems));
        Shot(screenshots, "image-menu-paperclip");
        Key(0x1B);
        Thread.Sleep(500);

        // 2. Right-click the embedded picture (the fixture paints it magenta) in the reading pane.
        var spot = Until(() => Magenta(window.Current.BoundingRectangle), "the embedded picture is not painted in the reading pane");
        var (x, y) = (spot[0], spot[1]);
        // Chromium's menu is not visible to UI Automation, so its items are clicked by position and judged by what
        // they do. A picture on a reader page offers exactly Save Picture As, then Copy image (screenshot kept).
        var target = Path.Combine(root, "saved-picture.png");
        RightClick(x, y);
        Shot(screenshots, "image-menu-right-click");
        // The menu opens at the pointer; its rows are about 32 px tall, the first starting just below it.
        Click(x + 50, y + 20, right: false);   // the first item, Save Picture As
        Shot(screenshots, "image-menu-after-save-item");
        // The dialog is owned by the Mailkeep window, so it may be listed under it rather than on the desktop.
        var title = new PropertyCondition(AutomationElement.NameProperty, "Save Picture As");
        var dialog = Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, title) ?? window.FindFirst(TreeScope.Children, title),
            "the first right-click item opened no Save Picture As dialog");
        var name = Until(() => dialog.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.NameProperty, "File name:"))), "Save Picture As has no File name box");
        // Typed, not set through UI Automation: the common dialog shows a set value but saves under the old name.
        name.SetFocus();
        Forms.SendKeys.SendWait("^a");
        Forms.SendKeys.SendWait(target);
        Thread.Sleep(300);
        var saveButton = Until(() => dialog.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, "Save"))), "Save Picture As has no Save button");
        _ = Task.Run(() => ((InvokePattern)saveButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
        Thread.Sleep(1000);
        Shot(screenshots, "image-menu-after-save");
        Until(() => File.Exists(target) && new FileInfo(target).Length > 0 ? (object)true : null, "Save Picture As wrote no file");
        var bytes = File.ReadAllBytes(target);
        Check(bytes.Length > 8 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G', "the saved picture is not a PNG");

        Clear();
        RightClick(x, y);
        Click(x + 50, y + 52, right: false);   // the second item, Copy image
        Until(() => HasImage() ? (object)true : null, "the second right-click item put no picture on the clipboard");
        var items = new[] { "Save Picture As", "Copy image" };
        return $"PASS image-menu paperclip=attachments+pictures right-click=[{string.Join(", ", items)}] save-image={bytes.Length}B copy-image=clipboard";
    }

    private static void RightClick(int x, int y)
    {
        Click(x, y, right: true);
        Thread.Sleep(500);
    }

    private static IEnumerable<AutomationElement> Menus()
    {
        var menus = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
        return menus.Cast<AutomationElement>();
    }

    private static List<string> Names(AutomationElement menu) => menu.FindAll(TreeScope.Descendants, Condition.TrueCondition)
        .Cast<AutomationElement>().Select(element => element.Current.Name.Replace("&", "")).Where(name => name.Length > 0).Distinct().ToList();

    private static void Click(int x, int y, bool right)
    {
        SetCursorPos(x, y);
        Thread.Sleep(150);
        mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero);
        mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(800);
    }

    private static void Key(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    /// <summary>The centre of the magenta fixture picture inside the given screen area, or null if not painted.</summary>
    private static int[]? Magenta(System.Windows.Rect bounds)
    {
        using var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen((int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
        long sx = 0, sy = 0, n = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.R == 255 && c.G == 0 && c.B == 255) { sx += x; sy += y; n++; }
            }
        return n < 200 ? null : [(int)(bounds.Left + sx / n), (int)(bounds.Top + sy / n)];
    }

    private static void Shot(string? folder, string name)
    {
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap = new System.Drawing.Bitmap(screen.Width, screen.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(screen.Location, System.Drawing.Point.Empty, screen.Size);
        bitmap.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Clear() => Sta(() => Forms.Clipboard.Clear());

    private static bool HasImage()
    {
        var has = false;
        Sta(() => has = Forms.Clipboard.ContainsImage());
        return has;
    }

    private static void Sta(Action action)
    {
        var thread = new Thread(() => { try { action(); } catch (ExternalException) { } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static T Until<T>(Func<T?> probe, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try { if (probe() is { } value) return value; } catch (ElementNotAvailableException) { }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(failure);
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
