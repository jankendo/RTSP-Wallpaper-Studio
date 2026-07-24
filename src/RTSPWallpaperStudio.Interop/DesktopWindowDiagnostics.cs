using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Public, read-only Win32 window inspection surface for the renderer and diagnostics.
/// Mutating desktop operations remain private to the attachment transaction.
/// </summary>
public static class DesktopWindowDiagnostics
{
    public static nint GetParent(nint hwnd) => NativeMethods.GetParent(hwnd);

    public static bool TryGetScreenRect(nint hwnd, out RectD rect)
    {
        if (NativeMethods.GetWindowRect(hwnd, out var nativeRect))
        {
            rect = RectD.FromBounds(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            return true;
        }

        rect = new RectD();
        return false;
    }
}
