using System.Runtime.InteropServices;

namespace RTSPWallpaperStudio.Renderer;

internal static class DesktopIconRegionProvider
{
    private const uint ProcessAccess = 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint LvmGetItemCount = 0x1004;
    private const uint LvmGetItemRect = 0x100E;
    private static readonly object Sync = new();
    private static DateTimeOffset _capturedAt;
    private static IReadOnlyList<IconRect> _cached = [];

    public static IReadOnlyList<IconRect> GetRelativeRects(nint rendererHwnd)
    {
        lock (Sync)
        {
            if (DateTimeOffset.UtcNow - _capturedAt < TimeSpan.FromSeconds(1))
            {
                return _cached;
            }

            _capturedAt = DateTimeOffset.UtcNow;
            _cached = Capture(rendererHwnd);
            return _cached;
        }
    }

    private static List<IconRect> Capture(nint rendererHwnd)
    {
        var progman = FindWindow("Progman", null);
        var shellView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        var listView = FindWindowEx(shellView, 0, "SysListView32", null);
        if (listView == 0 || !GetWindowRect(rendererHwnd, out var rendererRect))
        {
            return [];
        }

        _ = GetWindowThreadProcessId(listView, out var processId);
        var process = OpenProcess(ProcessAccess, false, processId);
        if (process == 0)
        {
            return [];
        }

        var remoteRect = VirtualAllocEx(process, 0, (nuint)Marshal.SizeOf<NativeRect>(), MemCommitReserve, PageReadWrite);
        try
        {
            if (remoteRect == 0)
            {
                return [];
            }

            var count = Math.Clamp((int)SendMessage(listView, LvmGetItemCount, 0, 0), 0, 4096);
            var result = new List<IconRect>(count * 2);
            for (var index = 0; index < count; index++)
            {
                // LVIR_ICON and LVIR_LABEL avoid the very wide LVIR_BOUNDS
                // rectangles returned by dense auto-arranged desktop views.
                foreach (var portion in new[] { 1, 2 })
                {
                    var rect = new NativeRect { Left = portion };
                    if (!WriteProcessMemory(process, remoteRect, ref rect, (nuint)Marshal.SizeOf<NativeRect>(), out _) ||
                        SendMessage(listView, LvmGetItemRect, (nuint)index, remoteRect) == 0 ||
                        !ReadProcessMemory(process, remoteRect, out rect, (nuint)Marshal.SizeOf<NativeRect>(), out _))
                    {
                        continue;
                    }

                    var points = new[]
                    {
                        new NativePoint { X = rect.Left, Y = rect.Top },
                        new NativePoint { X = rect.Right, Y = rect.Bottom }
                    };
                    _ = MapWindowPoints(listView, 0, points, 2);
                    result.Add(new IconRect(
                        points[0].X - rendererRect.Left,
                        points[0].Y - rendererRect.Top,
                        points[1].X - rendererRect.Left,
                        points[1].Y - rendererRect.Top,
                        portion == 1 ? (byte)0 : (byte)180));
                }
            }

            return result;
        }
        finally
        {
            if (remoteRect != 0) _ = VirtualFreeEx(process, remoteRect, 0, MemRelease);
            _ = CloseHandle(process);
        }
    }

    internal readonly record struct IconRect(int Left, int Top, int Right, int Bottom, byte Alpha);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(nint from, nint to, [In, Out] NativePoint[] points, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(nint process, nint address, ref NativeRect buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(nint process, nint address, out NativeRect buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
