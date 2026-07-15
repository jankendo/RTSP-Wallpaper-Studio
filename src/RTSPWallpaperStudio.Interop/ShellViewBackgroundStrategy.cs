using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Hosts the renderer as a child of SHELLDLL_DefView immediately behind its
/// SysListView32 icon surface. On Windows 11 shells where a Progman child
/// WorkerW is below Explorer's DirectComposition wallpaper visual, this is the
/// only child hierarchy that is simultaneously above the static wallpaper and
/// below desktop icons.
/// </summary>
public sealed class ShellViewBackgroundStrategy : IDesktopHostStrategy
{
    public DesktopLayoutStrategy Strategy => DesktopLayoutStrategy.ShellViewBackground;

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
        if (shellView == 0 || !NativeMethods.IsWindowVisible(shellView))
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopShellViewNotFound,
                "可視SHELLDLL_DefViewが見つかりません。", 0);
        }

        var sysList = NativeMethods.FindWindowEx(shellView, 0, "SysListView32", null);
        if (sysList == 0 || !NativeMethods.IsWindowVisible(sysList))
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopShellViewNotFound,
                "可視SysListView32が見つかりません。", 0);
        }

        if (NativeMethods.GetParent(shellView) != progman || NativeMethods.GetParent(sysList) != shellView)
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopUnsupportedLayout,
                "ShellViewとアイコン一覧の親子関係が現在のProgmanと一致しません。", 0);
        }

        return new DesktopHostDiscoveryResult(true, Strategy, progman, shellView, shellView, sysList,
            DesktopHostDiscovery.GetProcessId(progman),
            "SHELLDLL_DefView配下でSysListView32の直後を背景描画面として検出しました。");
    }

    public DesktopAttachResult Attach(nint rendererHwnd, MonitorInfo monitor)
    {
        var discovery = DiscoverExisting();
        return discovery.Success
            ? new DesktopAttachmentTransaction(discovery, monitor).Attach(rendererHwnd)
            : new DesktopAttachResult(false, RendererErrorCodes.DesktopHostNotFound,
                "Shell背景ホストを検出できません。", discovery.Diagnostic, null, discovery);
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
