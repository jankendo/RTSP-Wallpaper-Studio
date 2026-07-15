using System.Runtime.InteropServices;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Discovers Shell windows without mutating the desktop. The 0x052C message is only sent
/// by <see cref="EnsureDesktopHostCreated"/> during explicit construction or recovery.
/// </summary>
public sealed class DesktopHostDiscovery
{
    private readonly LegacyWorkerWStrategy _legacy = new();
    private readonly RaisedDesktopStrategy _raised = new();

    public void EnsureDesktopHostCreated()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == 0)
        {
            return;
        }

        NativeMethods.SendMessageTimeout(progman, NativeMethods.WmShellChange, 0xD, 0,
            NativeMethods.SendMessageTimeoutAbortIfHung, 1000, out _);
        NativeMethods.SendMessageTimeout(progman, NativeMethods.WmShellChange, 0xD, 1,
            NativeMethods.SendMessageTimeoutAbortIfHung, 1000, out _);
    }

    public DesktopHostDiscoveryResult DiscoverExisting()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == 0)
        {
            return Failure(DesktopLayoutStrategy.Unknown, "DESKTOP_PROGMAN_NOT_FOUND", "Progmanが見つかりません。", 0);
        }

        // Prefer the real shell-owned WorkerW surface. On the current Windows 11
        // shell the Progman child WorkerW (sibling of SHELLDLL_DefView) is the
        // only route that keeps icons in front of the renderer. A top-level
        // RaisedDesktop window can be visible while still covering the icon host.
        var legacy = _legacy.DiscoverExisting(progman);
        if (legacy.Success)
        {
            return WithCandidateDiagnostics(legacy);
        }

        // RaisedDesktop remains a compatibility candidate, but its shell
        // composition is validated after attach. It must never be accepted
        // solely because a non-black desktop pixel was observed.
        var raised = _raised.DiscoverExisting(progman);
        if (raised.Success)
        {
            return WithCandidateDiagnostics(raised);
        }

        return WithCandidateDiagnostics(Failure(
            DesktopLayoutStrategy.Unknown,
            "DESKTOP_HOST_NOT_FOUND",
            "アイコンを保持するShell構造と安全な壁紙ホストを判定できませんでした。",
            Math.Max(raised.LastError, legacy.LastError),
            $"RaisedDesktop: {raised.Diagnostic}; LegacyWorkerW: {legacy.Diagnostic}"));
    }

    internal static nint FindShellView(nint progman, out nint iconHost)
    {
        nint foundIconHost = 0;
        nint shellView = 0;

        // Prefer the shell view that is actually owned by this session's
        // Progman. A global EnumWindows scan can encounter stale or hidden
        // SHELLDLL_DefView windows from another shell surface first.
        var direct = NativeMethods.FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (direct != 0 && NativeMethods.IsWindowVisible(direct))
        {
            iconHost = progman;
            return direct;
        }

        NativeMethods.EnumWindows((window, _) =>
        {
            var windowShellView = NativeMethods.FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            if (windowShellView != 0 && NativeMethods.IsWindowVisible(windowShellView))
            {
                shellView = windowShellView;
                foundIconHost = window;
                return false;
            }

            NativeMethods.EnumChildWindows(window, (child, _) =>
            {
                if (!NativeMethods.GetClassNameSafe(child).Equals("SHELLDLL_DefView", StringComparison.Ordinal))
                {
                    return true;
                }

                if (NativeMethods.IsWindowVisible(child))
                {
                    shellView = child;
                    foundIconHost = NativeMethods.GetParent(child);
                    return false;
                }
                return true;
            }, 0);
            return shellView == 0;
        }, 0);
        iconHost = foundIconHost;
        return shellView;
    }

    internal static nint FindShellView(out nint iconHost) => FindShellView(NativeMethods.FindWindow("Progman", null), out iconHost);

    internal static bool IsForbiddenHost(nint hwnd)
    {
        var className = NativeMethods.GetClassNameSafe(hwnd);
        return className.Equals("Shell_TrayWnd", StringComparison.Ordinal) ||
               className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
               className.Equals("SHELLDLL_DefView", StringComparison.Ordinal) ||
               className.Equals("SysListView32", StringComparison.Ordinal);
    }

    internal static int GetProcessId(nint hwnd)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        _ = threadId;
        return unchecked((int)pid);
    }

    private static DesktopHostDiscoveryResult WithCandidateDiagnostics(DesktopHostDiscoveryResult result)
    {
        var candidates = new List<string>();
        var selected = new HashSet<nint>([result.ProgmanHwnd, result.HostHwnd, result.ShellViewHwnd, result.IconHostHwnd]);
        var seen = new HashSet<nint>();

        void Add(nint hwnd)
        {
            if (hwnd == 0 || !seen.Add(hwnd))
            {
                return;
            }

            var className = NativeMethods.GetClassNameSafe(hwnd);
            if (className is not ("Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
            {
                return;
            }

            var rejection = selected.Contains(hwnd)
                ? null
                : className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "SHELLDLL_DefView" or "SysListView32"
                    ? "forbidden-host"
                    : NativeMethods.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) != 0
                        ? "contains-shell-view"
                        : "not-selected";
            candidates.Add(NativeMethods.DescribeWindow(hwnd, selected.Contains(hwnd), rejection));
        }

        Add(result.ProgmanHwnd);
        NativeMethods.EnumWindows((window, _) =>
        {
            Add(window);
            NativeMethods.EnumChildWindows(window, (child, _) =>
            {
                Add(child);
                return true;
            }, 0);
            return true;
        }, 0);

        var suffix = candidates.Count == 0
            ? " candidates=[]"
            : $" candidates=[{string.Join(" || ", candidates.Take(64))}]";
        return result with { Diagnostic = result.Diagnostic + suffix };
    }

    internal static DesktopHostDiscoveryResult Failure(DesktopLayoutStrategy strategy, string code, string message, uint lastError, string? details = null) =>
        new(false, strategy, 0, 0, 0, 0, 0, $"{code}: {message} {details}", lastError);
}
