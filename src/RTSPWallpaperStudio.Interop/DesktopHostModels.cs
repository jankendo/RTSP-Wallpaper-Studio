using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

public sealed record DesktopHostDiscoveryResult(
    bool Success,
    DesktopLayoutStrategy Strategy,
    nint ProgmanHwnd,
    nint HostHwnd,
    nint ShellViewHwnd,
    nint IconHostHwnd,
    int ExplorerProcessId,
    string Diagnostic,
    uint LastError = 0);

public sealed record DesktopAttachResult(
    bool Success,
    string ErrorCode,
    string UserMessage,
    string TechnicalDetails,
    WindowSnapshot? Snapshot = null,
    DesktopHostDiscoveryResult? Discovery = null);

public sealed record WindowSnapshot(
    nint ParentHwnd,
    long Style,
    long ExtendedStyle,
    RectD ScreenRect,
    bool IsVisible,
    nint PreviousSibling,
    nint NextSibling);

public interface IDesktopHostStrategy
{
    DesktopLayoutStrategy Strategy { get; }
    DesktopHostDiscoveryResult DiscoverExisting();
    DesktopAttachResult Attach(nint rendererHwnd, MonitorInfo monitor);
    bool Validate(nint rendererHwnd, out string diagnostic);
    bool Rollback(nint rendererHwnd, WindowSnapshot snapshot, out string diagnostic);
}
