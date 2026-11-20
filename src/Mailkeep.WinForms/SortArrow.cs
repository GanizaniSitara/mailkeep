using System.Runtime.InteropServices;

namespace Mailkeep.WinForms;

/// <summary>The triangle in a sorted column's heading. A list view draws one itself only if the header is told
/// to, and WinForms exposes no way to tell it, so the header is asked directly — the same triangle Explorer
/// and the client this is modelled on both show, rather than an arrow typed into the heading's text.</summary>
internal static class SortArrow
{
    private const int LVM_GETHEADER = 0x1000 + 31;
    private const int HDM_GETITEM = 0x1200 + 11;
    private const int HDM_SETITEM = 0x1200 + 12;
    private const int HDI_FORMAT = 0x0004;
    private const int HDF_SORTUP = 0x0400;
    private const int HDF_SORTDOWN = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct HdItem
    {
        public int Mask;
        public int Width;
        public IntPtr Text;
        public IntPtr Bitmap;
        public int TextMax;
        public int Format;
        public IntPtr Param;
        public int Image;
        public int Order;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessage")]
    private static extern IntPtr SendItem(IntPtr handle, int message, IntPtr wParam, ref HdItem item);

    public static void Show(ListView list, int column, bool descending)
    {
        if (!list.IsHandleCreated) return;
        var header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero) return;
        for (var index = 0; index < list.Columns.Count; index++)
        {
            var item = new HdItem { Mask = HDI_FORMAT };
            if (SendItem(header, HDM_GETITEM, index, ref item) == IntPtr.Zero) continue;
            item.Format &= ~(HDF_SORTUP | HDF_SORTDOWN);
            if (index == column) item.Format |= descending ? HDF_SORTDOWN : HDF_SORTUP;
            SendItem(header, HDM_SETITEM, index, ref item);
        }
    }
}
