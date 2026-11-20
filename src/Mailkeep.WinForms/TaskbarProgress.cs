using System.Runtime.InteropServices;

namespace Mailkeep.WinForms;

/// <summary>Windows taskbar-button progress (the green fill) via ITaskbarList3. Every call is
/// best-effort: on shells without the interface, or before the button exists, it simply does nothing.
/// </summary>
internal static class TaskbarProgress
{
    private enum State { NoProgress = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, State state);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance { }

    private static readonly ITaskbarList3? Taskbar = StartupDiagnostics.Skip("TASKBAR") ? null
        : StartupDiagnostics.Time("Taskbar.Create", Create);

    private static ITaskbarList3? Create()
    {
        try
        {
            var instance = (ITaskbarList3)new TaskbarInstance();
            instance.HrInit();
            return instance;
        }
        catch { return null; }
    }

    public static void SetValue(IntPtr handle, int completed, int total)
    {
        try
        {
            StartupDiagnostics.Time("Taskbar.SetValue", () =>
            {
                Taskbar?.SetProgressState(handle, State.Normal);
                Taskbar?.SetProgressValue(handle, (ulong)completed, (ulong)total);
            });
        }
        catch { }
    }

    public static void SetError(IntPtr handle)
    {
        try { StartupDiagnostics.Time("Taskbar.SetError", () => Taskbar?.SetProgressState(handle, State.Error)); }
        catch { }
    }

    public static void Clear(IntPtr handle)
    {
        try { StartupDiagnostics.Time("Taskbar.Clear", () => Taskbar?.SetProgressState(handle, State.NoProgress)); }
        catch { }
    }
}
