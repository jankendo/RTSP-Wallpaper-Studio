using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Compatibility facade for callers that still use the old name. Health checks never send
/// the Shell creation message; construction/recovery is owned by DesktopHostController.
/// </summary>
public sealed class WorkerWLocator
{
    private readonly DesktopHostController _controller;

    public WorkerWLocator()
        : this(new DesktopHostController(new DesktopHostDiscovery()))
    {
    }

    public WorkerWLocator(DesktopHostController controller) => _controller = controller;

    public nint FindWallpaperHost() => _controller.DiscoverExisting().HostHwnd;

    public DesktopHostDiscoveryResult DiscoverForApply() => _controller.DiscoverForApply();

    public DesktopAttachResult TryAttach(nint rendererWindow, MonitorInfo monitor) => _controller.Attach(rendererWindow, monitor);

    public bool IsAttached(nint rendererWindow)
    {
        if (rendererWindow == 0 || !NativeMethods.IsWindow(rendererWindow))
        {
            return false;
        }

        return _controller.ValidateAttachment(rendererWindow, out _);
    }
}
