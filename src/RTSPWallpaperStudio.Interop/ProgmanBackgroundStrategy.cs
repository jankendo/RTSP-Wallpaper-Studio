using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Places the renderer as a Progman child immediately behind the icon-hosting
/// SHELLDLL_DefView and therefore ahead of the static-wallpaper WorkerW.
/// </summary>
public sealed class ProgmanBackgroundStrategy : IDesktopHostStrategy
{
    public DesktopLayoutStrategy Strategy => DesktopLayoutStrategy.ProgmanBackground;

    public DesktopHostDiscoveryResult DiscoverExisting() =>
        DiscoverExisting(NativeMethods.FindWindow("Progman", null));

    internal DesktopHostDiscoveryResult DiscoverExisting(nint progman)
    {
        if (progman == 0)
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopProgmanNotFound,
                "Progmanが見つかりません。", 0);
        }

        var shellView = DesktopHostDiscovery.FindShellView(progman, out _);
        var wallpaperWorker = NativeMethods.FindWindowEx(progman, 0, "WorkerW", null);
        if (shellView == 0 || NativeMethods.GetParent(shellView) != progman ||
            wallpaperWorker == 0 || NativeMethods.GetParent(wallpaperWorker) != progman)
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopUnsupportedLayout,
                "Progman直下のShellViewと静的壁紙WorkerWを同時に確認できません。", 0);
        }

        if (!NativeMethods.IsWindowVisible(shellView) || !NativeMethods.IsWindowVisible(wallpaperWorker))
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopUnsupportedLayout,
                "Progman背景候補が非表示です。", 0);
        }

        return new DesktopHostDiscoveryResult(true, Strategy, progman, progman, shellView, shellView,
            DesktopHostDiscovery.GetProcessId(progman),
            $"Progman直下でShellView(0x{shellView.ToInt64():X})と静的壁紙WorkerW(0x{wallpaperWorker.ToInt64():X})の間を検出しました。");
    }

    public DesktopAttachResult Attach(nint rendererHwnd, MonitorInfo monitor)
    {
        var discovery = DiscoverExisting();
        return discovery.Success
            ? new DesktopAttachmentTransaction(discovery, monitor).Attach(rendererHwnd)
            : new DesktopAttachResult(false, RendererErrorCodes.DesktopHostNotFound,
                "Progman背景ホストを検出できません。", discovery.Diagnostic, null, discovery);
    }

    public bool Validate(nint rendererHwnd, out string diagnostic)
    {
        var discovery = DiscoverExisting();
        if (!discovery.Success)
        {
            diagnostic = discovery.Diagnostic;
            return false;
        }

        return DesktopAttachmentValidator.Validate(rendererHwnd, discovery, out diagnostic);
    }

    public bool Rollback(nint rendererHwnd, WindowSnapshot snapshot, out string diagnostic) =>
        DesktopAttachmentTransaction.Rollback(rendererHwnd, snapshot, out diagnostic);
}
