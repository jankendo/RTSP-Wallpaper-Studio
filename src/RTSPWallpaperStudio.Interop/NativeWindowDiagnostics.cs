namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Read-only Win32 inspection used by renderer metrics and the desktop probe.
/// Keeping these values in the event stream makes a WallpaperVisible event auditable
/// without relying on a screenshot or on the UI process's assumptions.
/// </summary>
public static class NativeWindowDiagnostics
{
    public static bool IsWindow(nint hwnd) => hwnd != 0 && NativeMethods.IsWindow(hwnd);

    public static bool IsVisible(nint hwnd) => hwnd != 0 && NativeMethods.IsWindowVisible(hwnd);

    public static int GetProcessId(nint hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return unchecked((int)processId);
    }

    public static long GetStyle(nint hwnd) => NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();

    public static long GetExtendedStyle(nint hwnd) => NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlexStyle).ToInt64();

    public static nint GetParent(nint hwnd) => NativeMethods.GetParent(hwnd);

    public static nint GetRoot(nint hwnd) => NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);

    public static nint GetOwner(nint hwnd) => NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndOwner);

    public static string GetClassName(nint hwnd) => NativeMethods.GetClassNameSafe(hwnd);

    public static string Describe(nint hwnd, bool selected = false, string? rejection = null) =>
        NativeMethods.DescribeWindow(hwnd, selected, rejection);
}
