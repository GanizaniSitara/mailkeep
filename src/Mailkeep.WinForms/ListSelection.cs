using System.Runtime.InteropServices;

namespace Mailkeep.WinForms;

/// <summary>Select All for a list that holds no items of its own. A virtual list is handed the rows it draws,
/// so there is nothing to walk and mark, and marking a hundred thousand rows one at a time through
/// SelectedIndices would take long enough to notice. The list view has always understood "all items" itself,
/// so it is asked once and answers with a single range.</summary>
internal static class ListSelection
{
    private const int LVM_SETITEMSTATE = 0x1000 + 43;
    private const int LVIS_SELECTED = 0x0002;
    private const int AllItems = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LvItem
    {
        public int Mask;
        public int Item;
        public int SubItem;
        public int State;
        public int StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr Param;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessage")]
    private static extern IntPtr SendItem(IntPtr handle, int message, IntPtr wParam, ref LvItem item);

    public static void SelectAll(ListView list)
    {
        if (!list.IsHandleCreated || !list.MultiSelect) return;
        Set(list, LVIS_SELECTED);
    }

    /// <summary>Drops the whole selection, in the same single message. A virtual list holds its selection as
    /// bare indices, so this has to happen while those indices still mean something.</summary>
    public static void ClearSelection(ListView list)
    {
        if (!list.IsHandleCreated) return;
        Set(list, 0);
    }

    private static void Set(ListView list, int state)
    {
        var item = new LvItem { State = state, StateMask = LVIS_SELECTED };
        SendItem(list.Handle, LVM_SETITEMSTATE, AllItems, ref item);
    }
}
