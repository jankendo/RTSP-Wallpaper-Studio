using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

public sealed class DesktopHostController
{
    private readonly DesktopHostDiscovery _discovery;
    private DesktopHostDiscoveryResult? _lastDiscovery;

    public DesktopHostController(DesktopHostDiscovery discovery)
    {
        _discovery = discovery;
    }

    public DesktopHostDiscoveryResult DiscoverForApply()
    {
        var result = _discovery.DiscoverExisting();
        if (!result.Success)
        {
            _discovery.EnsureDesktopHostCreated();
            result = _discovery.DiscoverExisting();
        }

        _lastDiscovery = result;
        return result;
    }

    public DesktopHostDiscoveryResult DiscoverExisting() => _lastDiscovery ?? _discovery.DiscoverExisting();

    public DesktopAttachResult Attach(nint rendererHwnd, MonitorInfo monitor)
    {
        var discovery = DiscoverForApply();
        if (!discovery.Success)
        {
            return new(false, RendererErrorCodes.DesktopHostNotFound,
                "デスクトップの安全な壁紙構造を判定できません。Rendererは表示しません。", discovery.Diagnostic, null, discovery);
        }

        var strategy = discovery.Strategy == DesktopLayoutStrategy.RaisedDesktop
            ? (IDesktopHostStrategy)new RaisedDesktopStrategy()
            : new LegacyWorkerWStrategy();
        var result = strategy.Attach(rendererHwnd, monitor);
        _lastDiscovery = result.Discovery ?? discovery;
        return result;
    }

    public bool ValidateAttachment(nint rendererHwnd, out string diagnostic)
    {
        var discovery = DiscoverExisting();
        if (!discovery.Success)
        {
            diagnostic = discovery.Diagnostic;
            return false;
        }

        return DesktopAttachmentValidator.Validate(rendererHwnd, discovery, out diagnostic);
    }

    public void Invalidate()
    {
        _lastDiscovery = null;
    }
}
