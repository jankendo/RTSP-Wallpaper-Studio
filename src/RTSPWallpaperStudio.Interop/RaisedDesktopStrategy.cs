using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

public sealed class RaisedDesktopStrategy : IDesktopHostStrategy
{
    public DesktopLayoutStrategy Strategy => DesktopLayoutStrategy.RaisedDesktop;

    public DesktopHostDiscoveryResult DiscoverExisting() => DiscoverExisting(NativeMethods.FindWindow("Progman", null));

    internal DesktopHostDiscoveryResult DiscoverExisting(nint progman)
    {
        if (progman == 0)
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopProgmanNotFound, "Progmanが見つかりません。", 0);
        }

        var shellView = NativeMethods.FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (shellView == 0 || !NativeMethods.HasExtendedStyle(progman, NativeMethods.WsExNoRedirectionBitmap))
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopUnsupportedLayout,
                "Raised Desktop構造ではありません。", 0);
        }

        return new DesktopHostDiscoveryResult(true, Strategy, progman, progman, shellView, progman,
            DesktopHostDiscovery.GetProcessId(progman), "Windows 11 Raised Desktop構造を検出しました。");
    }

    public DesktopAttachResult Attach(nint rendererHwnd, MonitorInfo monitor)
    {
        var discovery = DiscoverExisting();
        if (!discovery.Success)
        {
            return new(false, RendererErrorCodes.DesktopUnsupportedLayout, "対応していないRaised Desktop構造です。", discovery.Diagnostic, null, discovery);
        }

        var transaction = new DesktopAttachmentTransaction(discovery, monitor);
        return transaction.Attach(rendererHwnd);
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
