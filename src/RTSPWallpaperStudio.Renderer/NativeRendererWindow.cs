using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RTSPWallpaperStudio.Renderer;

internal sealed class NativeRendererWindow : IDisposable
{
    internal const string ClassName = "RTSPWallpaperStudio.RendererHost";
    private static readonly RendererWin32.WndProcDelegate WndProc = WindowProc;
    private static ushort _classAtom;
    private bool _disposed;

    public NativeRendererWindow()
    {
        RegisterClass();
        var instance = RendererWin32.GetModuleHandle(null);
        Hwnd = RendererWin32.CreateWindowEx(
            (int)(RendererWin32.WsExToolWindow | RendererWin32.WsExNoActivate),
            ClassName,
            null,
            (int)RendererWin32.WsPopup,
            0,
            0,
            1,
            1,
            0,
            0,
            instance,
            0);
        if (Hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Renderer HWNDの作成に失敗しました。");
        }

        RendererWin32.ShowWindow(Hwnd, RendererWin32.SwHide);
    }

    public nint Hwnd { get; }

    public void ShowAfterValidation()
    {
        EnsureNotDisposed();
        RendererWin32.ShowWindow(Hwnd, RendererWin32.SwShow);
    }

    public void Hide()
    {
        if (!_disposed && Hwnd != 0)
        {
            RendererWin32.ShowWindow(Hwnd, RendererWin32.SwHide);
        }
    }

    public void CloseFromAnyThread()
    {
        if (!_disposed && Hwnd != 0)
        {
            RendererWin32.PostMessage(Hwnd, RendererWin32.WmClose, 0, 0);
        }
    }

    public void ResetToHiddenTopLevel()
    {
        EnsureNotDisposed();
        Hide();
        RendererWin32.SetParent(Hwnd, 0);
        var style = RendererWin32.GetWindowLongPtr(Hwnd, RendererWin32.GwlStyle).ToInt64();
        style = (style | RendererWin32.WsPopup | RendererWin32.WsClipChildren | RendererWin32.WsClipSiblings) & ~RendererWin32.WsChild;
        RendererWin32.SetWindowLongPtr(Hwnd, RendererWin32.GwlStyle, new nint(style));
        RendererWin32.SetWindowPos(Hwnd, 0, 0, 0, 1, 1,
            RendererWin32.SwpNoActivate | RendererWin32.SwpNoZOrder | RendererWin32.SwpFrameChanged);
    }

    public static void RunMessageLoop()
    {
        while (true)
        {
            var result = RendererWin32.GetMessage(out var message, 0, 0, 0);
            if (result <= 0)
            {
                return;
            }

            RendererWin32.TranslateMessage(ref message);
            RendererWin32.DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Hwnd != 0)
        {
            RendererWin32.DestroyWindow(Hwnd);
        }
    }

    private static void RegisterClass()
    {
        if (_classAtom != 0)
        {
            return;
        }

        var wndClass = new RendererWin32.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<RendererWin32.WndClassEx>(),
            WndProc = WndProc,
            Instance = RendererWin32.GetModuleHandle(null),
            ClassName = ClassName
        };
        _classAtom = RendererWin32.RegisterClassEx(ref wndClass);
        if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Rendererウィンドウクラスの登録に失敗しました。");
        }
    }

    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == RendererWin32.WmDestroy)
        {
            RendererWin32.PostQuitMessage(0);
        }

        if (message == RendererWin32.WmNcDestroy)
        {
            return 0;
        }

        return RendererWin32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
