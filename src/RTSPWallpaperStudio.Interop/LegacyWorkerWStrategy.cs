using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

public sealed class LegacyWorkerWStrategy : IDesktopHostStrategy
{
    public DesktopLayoutStrategy Strategy => DesktopLayoutStrategy.LegacyWorkerW;

    public DesktopHostDiscoveryResult DiscoverExisting() => DiscoverExisting(NativeMethods.FindWindow("Progman", null));

    internal DesktopHostDiscoveryResult DiscoverExisting(nint progman)
    {
        if (progman == 0)
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopProgmanNotFound, "Progmanが見つかりません。", 0);
        }

        var shellView = DesktopHostDiscovery.FindShellView(out var iconHost);
        if (shellView == 0 || iconHost == 0)
        {
            return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopShellViewNotFound, "SHELLDLL_DefViewが見つかりません。", 0);
        }

        if (iconHost == progman || NativeMethods.GetClassNameSafe(iconHost).Equals("WorkerW", StringComparison.Ordinal))
        {
            var candidate = NativeMethods.GetWindow(iconHost, NativeMethods.GwHwndNext);
            while (candidate != 0)
            {
                var className = NativeMethods.GetClassNameSafe(candidate);
                if (className.Equals("WorkerW", StringComparison.Ordinal) &&
                    NativeMethods.FindWindowEx(candidate, 0, "SHELLDLL_DefView", null) == 0 &&
                    !DesktopHostDiscovery.IsForbiddenHost(candidate))
                {
                    return new DesktopHostDiscoveryResult(true, Strategy, progman, candidate, shellView, iconHost,
                        DesktopHostDiscovery.GetProcessId(progman), "Legacy WorkerWを検出しました。");
                }

                candidate = NativeMethods.GetWindow(candidate, NativeMethods.GwHwndNext);
            }
        }

        var afterIconHost = NativeMethods.FindWindowEx(0, iconHost, "WorkerW", null);
        if (afterIconHost != 0 && NativeMethods.FindWindowEx(afterIconHost, 0, "SHELLDLL_DefView", null) == 0)
        {
            return new DesktopHostDiscoveryResult(true, Strategy, progman, afterIconHost, shellView, iconHost,
                DesktopHostDiscovery.GetProcessId(progman), "Legacy WorkerWを検出しました。");
        }

        return DesktopHostDiscovery.Failure(Strategy, RendererErrorCodes.DesktopHostNotFound,
            "アイコン用ShellViewの背面に安全なWorkerWがありません。", 0);
    }

    public DesktopAttachResult Attach(nint rendererHwnd, MonitorInfo monitor)
    {
        var discovery = DiscoverExisting();
        if (!discovery.Success)
        {
            return new(false, RendererErrorCodes.DesktopHostNotFound, "壁紙ホストを検出できません。", discovery.Diagnostic, null, discovery);
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
