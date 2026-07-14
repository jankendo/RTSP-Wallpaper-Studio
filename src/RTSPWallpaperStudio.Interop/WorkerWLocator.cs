using System.Diagnostics;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// WorkerW is an undocumented Shell layout detail. Windows updates can change this behavior,
/// so every attachment is verified and the caller receives a diagnosable failure.
/// </summary>
public sealed class WorkerWLocator
{
    private const uint WmShellChange = 0x052C;

    public nint FindWallpaperHost()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == 0)
        {
            return 0;
        }

        NativeMethods.SendMessageTimeout(progman, WmShellChange, 0xD, 0, NativeMethods.SendMessageTimeoutAbortIfHung, 1000, out _);
        NativeMethods.SendMessageTimeout(progman, WmShellChange, 0xD, 1, NativeMethods.SendMessageTimeoutAbortIfHung, 1000, out _);

        nint shellView = 0;
        nint wallpaperHost = 0;
        NativeMethods.EnumWindows((window, _) =>
        {
            var candidate = NativeMethods.FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            if (candidate == 0)
            {
                return true;
            }

            shellView = candidate;
            wallpaperHost = NativeMethods.FindWindowEx(0, window, "WorkerW", null);
            return false;
        }, 0);

        if (wallpaperHost == 0 && shellView != 0)
        {
            wallpaperHost = NativeMethods.FindWindowEx(0, shellView, "WorkerW", null);
        }

        return wallpaperHost;
    }

    public bool TryAttach(nint rendererWindow, MonitorInfo monitor, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (rendererWindow == 0 || !NativeMethods.IsWindow(rendererWindow))
        {
            diagnostic = "Rendererウィンドウのハンドルが無効です。";
            return false;
        }

        var workerW = FindWallpaperHost();
        if (workerW == 0 || !NativeMethods.IsWindow(workerW))
        {
            diagnostic = "WorkerWを検出できませんでした。Explorerの状態を確認してください。";
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(rendererWindow, NativeMethods.GwlStyle).ToInt64();
        style = (style | NativeMethods.WsChild | NativeMethods.WsVisible | NativeMethods.WsClipChildren | NativeMethods.WsClipSiblings) & ~NativeMethods.WsPopup;
        NativeMethods.SetWindowLongPtr(rendererWindow, NativeMethods.GwlStyle, new nint(style));

        var exStyle = NativeMethods.GetWindowLongPtr(rendererWindow, NativeMethods.GwExStyle).ToInt64();
        exStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(rendererWindow, NativeMethods.GwExStyle, new nint(exStyle));

        if (NativeMethods.SetParent(rendererWindow, workerW) == 0 && Debugger.IsAttached)
        {
            diagnostic = "SetParentが失敗しました。";
            return false;
        }

        var bounds = monitor.Bounds;
        if (!NativeMethods.SetWindowPos(rendererWindow, 0, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height,
                NativeMethods.SetWindowPosNoActivate | NativeMethods.SetWindowPosNoOwnerZOrder | NativeMethods.SetWindowPosShowWindow))
        {
            diagnostic = "壁紙ウィンドウのサイズ設定に失敗しました。";
            return false;
        }

        return true;
    }

    public bool IsAttached(nint rendererWindow)
    {
        var workerW = FindWallpaperHost();
        return rendererWindow != 0 && workerW != 0 && NativeMethods.IsWindow(rendererWindow);
    }
}
