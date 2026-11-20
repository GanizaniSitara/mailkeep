using System.Reflection;
using System.Runtime.InteropServices;

namespace Mailkeep.WinForms;

/// <summary>
/// A list view that paints itself off-screen. An owner-drawn or virtual ListView repaints
/// a row at a time or flashes on background erasing; setting LVS_EX_DOUBLEBUFFER on the native
/// handle and enabling WinForms double-buffering ensures clean, flicker-free rendering.
/// </summary>
internal class BufferedListView : ListView
{
    private const int LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1000 + 54;
    private const int LVS_EX_DOUBLEBUFFER = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    public BufferedListView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private const int LVM_REDRAWITEMS = 0x1000 + 21;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)LVS_EX_DOUBLEBUFFER);
    }

    /// <summary>
    /// Forces native virtual ListView (LVS_OWNERDATA) to discard its cached item renderings and
    /// request LVN_GETDISPINFO for all items in the given range or all items, ensuring immediate
    /// repaint without requiring VirtualListSize = 0 resets or scroll jumps.
    /// </summary>
    public void RedrawItems(int first = 0, int last = -1)
    {
        if (!IsHandleCreated) return;
        if (VirtualListSize == 0)
        {
            Invalidate();
            Update();
            return;
        }
        if (last < 0 || last >= VirtualListSize) last = VirtualListSize - 1;
        if (first < 0) first = 0;
        if (first <= last)
        {
            SendMessage(Handle, LVM_REDRAWITEMS, (IntPtr)first, (IntPtr)last);
            Invalidate();
            Update();
        }
    }
}

/// <summary>
/// A tree view that paints itself off-screen. Owner-drawn tree views or standard tree views
/// erase their background on WM_ERASEBKGND before drawing items, which causes visible flickering
/// on node selection, collapse/expand, count updates, scrolling, and resizing. TVS_EX_DOUBLEBUFFER
/// and WinForms double-buffering eliminate this completely.
/// </summary>
internal class BufferedTreeView : TreeView
{
    private const int TVM_SETEXTENDEDSTYLE = 0x1100 + 44;
    private const int TVS_EX_DOUBLEBUFFER = 0x0004;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    public BufferedTreeView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, TVM_SETEXTENDEDSTYLE, (IntPtr)TVS_EX_DOUBLEBUFFER, (IntPtr)TVS_EX_DOUBLEBUFFER);
    }
}

/// <summary>
/// A split container whose panels and surface are double buffered, preventing background-erase
/// flicker when dragging the splitter or resizing the window.
/// </summary>
internal class BufferedSplitContainer : SplitContainer
{
    public BufferedSplitContainer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BufferedControls.EnableDoubleBuffering(Panel1);
        BufferedControls.EnableDoubleBuffering(Panel2);
    }
}

/// <summary>
/// A TableLayoutPanel that enables double buffering, eliminating row/column cell clearing flicker
/// during window resizing, splitter movement, or child layout changes.
/// </summary>
internal class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

/// <summary>
/// A Panel that enables double buffering to eliminate background erasure flashes.
/// </summary>
internal class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

/// <summary>
/// A Form that enables double buffering, eliminating flicker on resize and repaint.
/// </summary>
internal class BufferedForm : Form
{
    public BufferedForm()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

internal static class BufferedControls
{
    public static void EnableDoubleBuffering(Control control)
    {
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(control, true, null);
    }
}
