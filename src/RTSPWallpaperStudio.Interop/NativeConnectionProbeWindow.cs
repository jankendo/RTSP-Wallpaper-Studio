using System.Runtime.InteropServices;

namespace RTSPWallpaperStudio.Interop;

/// <summary>LibVLCの接続検証専用。表示・タスクバー登録・WorkerWへの親子付けを行わない。</summary>
public sealed class NativeConnectionProbeWindow : IDisposable
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsPopup = unchecked((int)0x80000000);

    public NativeConnectionProbeWindow()
    {
        Hwnd = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            "STATIC",
            "RTSPWallpaperStudio.ConnectionProbe",
            WsPopup,
            0, 0, 1, 1,
            nint.Zero, nint.Zero, GetModuleHandle(null), nint.Zero);

        if (Hwnd == nint.Zero)
        {
            throw new InvalidOperationException("接続テスト用の非表示Win32ウィンドウを作成できませんでした。");
        }
    }

    public nint Hwnd { get; }

    public void Dispose()
    {
        if (Hwnd != nint.Zero)
        {
            DestroyWindow(Hwnd);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
