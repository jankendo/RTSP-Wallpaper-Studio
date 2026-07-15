using System.Runtime.InteropServices;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Read-only verification of the Windows desktop composition path. The probe
/// never clicks, sends keyboard input, changes focus, or mutates Shell data.
/// </summary>
public static class DesktopShellCompositionProbe
{
    private const uint DwmCloaked = 14;
    private static readonly string[] RelevantClasses =
    [
        "Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32",
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "RTSPWallpaperStudio.RendererHost"
    ];

    public static DesktopShellCompositionProbeResult Capture(
        DesktopHostDiscoveryResult discovery,
        nint rendererHwnd,
        bool rendererFramesVisibleOnDesktop)
    {
        var windows = CaptureWindows(rendererHwnd);
        var shellView = discovery.ShellViewHwnd;
        var sysList = FindDescendant(shellView, "SysListView32");
        var rendererParent = rendererHwnd == 0 ? 0 : NativeMethods.GetParent(rendererHwnd);
        var rendererRoot = rendererHwnd == 0 ? 0 : NativeMethods.GetAncestor(rendererHwnd, NativeMethods.GaRoot);
        var shellViewVisible = IsVisibleAndUncloaked(shellView);
        var sysListVisible = IsVisibleAndUncloaked(sysList);
        var iconHostLocated = shellView != 0 && sysList != 0;
        var iconHostVisible = iconHostLocated && shellViewVisible && sysListVisible;
        var iconCount = sysList == 0 ? 0 : ReadIconCount(sysList);

        var iconAnchor = DirectChildUnder(discovery.ProgmanHwnd, shellView);
        var rendererAnchor = DirectChildUnder(discovery.ProgmanHwnd, rendererHwnd);
        var iconsAboveRenderer = iconAnchor != 0 && rendererAnchor != 0 &&
                                 IsAboveSibling(iconAnchor, rendererAnchor);
        var shellViewIndex = GetZOrderIndex(shellView);
        var rendererIndex = GetZOrderIndex(rendererHwnd);
        var sysListIndex = GetZOrderIndex(sysList);

        var rendererRect = TryGetRect(rendererHwnd, out var rendererNativeRect)
            ? ToRectD(rendererNativeRect)
            : new RectD();
        var visibleTaskbars = windows
            .Where(x => x.ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            .Where(x => x.Visible && !x.Cloaked && x.Rect is not null);
        var taskbars = (rendererHwnd == 0
                ? visibleTaskbars
                : visibleTaskbars.Where(x => Intersects(x.Rect!.Value, rendererRect)))
            .ToArray();
        var taskbarLocated = windows.Any(x => x.ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
        var taskbarVisible = taskbars.Length > 0;
        var rendererTopLevel = rendererRoot == 0 ? 0 : rendererRoot;
        var taskbarAbove = taskbars.Length > 0 && taskbars.All(x =>
            IsAboveTopLevel((nint)x.Hwnd, rendererTopLevel));
        var taskbar = taskbars.FirstOrDefault();
        var taskbarIndex = taskbar?.ZOrderIndex ?? -1;

        var exStyle = rendererHwnd == 0 ? 0 : NativeMethods.GetWindowLongPtr(rendererHwnd, NativeMethods.GwlexStyle).ToInt64();
        var style = rendererHwnd == 0 ? 0 : NativeMethods.GetWindowLongPtr(rendererHwnd, NativeMethods.GwlStyle).ToInt64();
        var rendererNotInAltTab = rendererHwnd != 0 &&
                                  (exStyle & NativeMethods.WsExToolWindow) != 0 &&
                                  (exStyle & NativeMethods.WsExAppWindow) == 0 &&
                                  ((style & NativeMethods.WsChild) != 0 ||
                                   NativeMethods.GetClassNameSafe(rendererRoot) == "Progman");
        var rendererNotInTaskbar = rendererHwnd != 0 &&
                                   (exStyle & NativeMethods.WsExAppWindow) == 0 &&
                                   !HasTaskbarAncestor(rendererHwnd);
        var rendererDoesNotOwnForeground = rendererHwnd != 0 &&
                                           !IsRendererForeground(NativeMethods.GetForegroundWindow(), rendererHwnd) &&
                                           !IsRendererForeground(NativeMethods.GetActiveWindow(), rendererHwnd) &&
                                           !IsRendererForeground(NativeMethods.GetFocus(), rendererHwnd);

        var hitTests = ProbeInput(shellView, sysList, taskbar?.Hwnd ?? 0, rendererHwnd);
        var composition = new RendererShellCompositionMetrics(
            RendererFramesVisibleOnDesktop: rendererFramesVisibleOnDesktop,
            DesktopIconHostLocated: iconHostLocated,
            DesktopIconHostVisible: iconHostVisible,
            DesktopIconsAboveRenderer: iconsAboveRenderer,
            TaskbarLocated: taskbarLocated,
            TaskbarVisible: taskbarVisible,
            TaskbarAboveRenderer: taskbarAbove,
            RendererNotInAltTab: rendererNotInAltTab,
            RendererNotInTaskbar: rendererNotInTaskbar,
            RendererDoesNotOwnForeground: rendererDoesNotOwnForeground,
            DesktopInputAvailable: hitTests.Available,
            IsCompositionVerified: false,
            RendererHwnd: rendererHwnd.ToInt64(),
            RendererParentHwnd: rendererParent.ToInt64(),
            RendererRootHwnd: rendererRoot.ToInt64(),
            ShellHostHwnd: discovery.HostHwnd.ToInt64(),
            ShellViewHwnd: shellView.ToInt64(),
            SysListViewHwnd: sysList.ToInt64(),
            TaskbarHwnd: taskbar?.Hwnd ?? 0,
            InputHitTestHwnd: hitTests.HitHwnd,
            DesktopIconCount: iconCount,
            RendererZOrderIndex: rendererIndex,
            ShellViewZOrderIndex: shellViewIndex,
            SysListViewZOrderIndex: sysListIndex,
            TaskbarZOrderIndex: taskbarIndex,
            Diagnostic: BuildDiagnostic(discovery, iconAnchor, rendererAnchor, taskbars, hitTests));
        composition = composition with { IsCompositionVerified = RendererShellCompositionContract.IsVerified(composition) };
        return new DesktopShellCompositionProbeResult(composition, windows, hitTests.Diagnostic);
    }

    public static DesktopShellCompositionProbeResult CaptureShellHierarchy()
    {
        var discovery = new DesktopHostDiscovery().DiscoverExisting();
        return Capture(discovery, 0, false);
    }

    private static List<ShellWindowSnapshot> CaptureWindows(nint rendererHwnd)
    {
        var result = new List<ShellWindowSnapshot>();
        var seen = new HashSet<nint>();

        void Add(nint hwnd)
        {
            if (hwnd == 0 || !seen.Add(hwnd))
            {
                return;
            }

            var className = NativeMethods.GetClassNameSafe(hwnd);
            if (hwnd != rendererHwnd && !RelevantClasses.Contains(className, StringComparer.Ordinal))
            {
                return;
            }

            var visible = NativeMethods.IsWindowVisible(hwnd);
            var cloaked = IsCloaked(hwnd);
            RectD? rect = TryGetRect(hwnd, out var nativeRect) ? (RectD?)ToRectD(nativeRect) : null;
            var monitorRect = TryGetMonitorRect(hwnd);
            result.Add(new ShellWindowSnapshot(
                hwnd.ToInt64(), className, NativeMethods.GetWindowTextSafe(hwnd),
                GetProcessId(hwnd), NativeMethods.GetWindowThreadProcessId(hwnd, out _),
                NativeMethods.GetParent(hwnd).ToInt64(), NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndOwner).ToInt64(),
                NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot).ToInt64(),
                NativeMethods.GetAncestor(hwnd, NativeMethods.GaRootOwner).ToInt64(),
                NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndPrev).ToInt64(),
                NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndNext).ToInt64(),
                NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64(),
                NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlexStyle).ToInt64(),
                visible, NativeMethods.IsWindowEnabled(hwnd), cloaked, rect, monitorRect,
                GetZOrderIndex(hwnd)));
        }

        NativeMethods.EnumWindows((top, _) =>
        {
            Add(top);
            NativeMethods.EnumChildWindows(top, (child, _) =>
            {
                Add(child);
                return true;
            }, 0);
            return true;
        }, 0);

        if (rendererHwnd != 0 && !seen.Contains(rendererHwnd))
        {
            Add(rendererHwnd);
        }

        return result;
    }

    private static InputProbeResult ProbeInput(nint shellView, nint sysList, long taskbar, nint renderer)
    {
        var rendererRect = renderer != 0 && TryGetRect(renderer, out var nativeRendererRect)
            ? (RectD?)ToRectD(nativeRendererRect)
            : null;
        var desktopPointValid = ProbePoint(sysList != 0 ? sysList : shellView, shellView, renderer, rendererRect, true, out var shellHwnd, out var shellHit);
        nint taskbarHwnd = 0;
        var taskbarHit = false;
        var taskbarPointValid = taskbar != 0 && ProbePoint((nint)taskbar, (nint)taskbar, renderer, null, false, out taskbarHwnd, out taskbarHit);
        var hit = shellHit ? shellHwnd : taskbarHwnd;
        return new(desktopPointValid && taskbarPointValid, hit.ToInt64(),
            $"desktopPointValid={desktopPointValid};shellHit={shellHit};shellHwnd=0x{shellHwnd.ToInt64():X};shellClass={NativeMethods.GetClassNameSafe(shellHwnd)};taskbarPointValid={taskbarPointValid};taskbarHit={taskbarHit};taskbarHwnd=0x{taskbarHwnd.ToInt64():X};taskbarClass={NativeMethods.GetClassNameSafe(taskbarHwnd)}");
    }

    private static bool ProbePoint(nint target, nint acceptedRoot, nint renderer, RectD? preferredRect,
        bool allowNonShellWindow, out nint hit, out bool shellHit)
    {
        hit = 0;
        shellHit = false;
        if (target == 0 || !TryGetRect(target, out var rect))
        {
            return false;
        }

        var targetRect = ToRectD(rect);
        var pointRect = preferredRect is { } preferred && Intersects(targetRect, preferred) ? preferred : targetRect;
        var point = new NativeMethods.Point
        {
            X = (int)Math.Round(pointRect.X + pointRect.Width / 2),
            // Avoid a taskbar at the bottom of the target monitor when a
            // full virtual-desktop ShellView is being probed.
            Y = (int)Math.Round(pointRect.Y + pointRect.Height * 0.35)
        };
        hit = NativeMethods.WindowFromPoint(point);
        if (hit == renderer || IsDescendantOrSelf(hit, renderer))
        {
            return false;
        }

        shellHit = hit != 0 && IsDescendantOrSelf(hit, acceptedRoot);
        // A normal foreground application can legitimately occupy the
        // desktop sample point. In that case the important safety fact is
        // that the renderer did not intercept the point; taskbar points are
        // still required to resolve to the Shell taskbar itself.
        return shellHit || (allowNonShellWindow && hit != 0);
    }

    private static string BuildDiagnostic(DesktopHostDiscoveryResult discovery, nint iconAnchor,
        nint rendererAnchor, ShellWindowSnapshot[] taskbars, InputProbeResult input) =>
        $"strategy={discovery.Strategy};progman=0x{discovery.ProgmanHwnd.ToInt64():X};host=0x{discovery.HostHwnd.ToInt64():X};" +
        $"shellView=0x{discovery.ShellViewHwnd.ToInt64():X};iconAnchor=0x{iconAnchor.ToInt64():X};" +
        $"rendererAnchor=0x{rendererAnchor.ToInt64():X};taskbars={taskbars.Length};input={input.Diagnostic};" +
        $"foreground=0x{NativeMethods.GetForegroundWindow().ToInt64():X};active=0x{NativeMethods.GetActiveWindow().ToInt64():X};focus=0x{NativeMethods.GetFocus().ToInt64():X}";

    private static bool IsAboveSibling(nint upper, nint lower)
    {
        var parent = NativeMethods.GetParent(upper);
        return parent == NativeMethods.GetParent(lower) &&
               GetZOrderIndex(upper) >= 0 && GetZOrderIndex(lower) >= 0 &&
               GetZOrderIndex(upper) < GetZOrderIndex(lower);
    }

    private static bool IsAboveTopLevel(nint upper, nint lower)
    {
        var upperRoot = NativeMethods.GetAncestor(upper, NativeMethods.GaRoot);
        var lowerRoot = NativeMethods.GetAncestor(lower, NativeMethods.GaRoot);
        if (upperRoot == 0 || lowerRoot == 0)
        {
            return false;
        }

        return IsAboveSibling(upperRoot, lowerRoot);
    }

    private static nint DirectChildUnder(nint root, nint hwnd)
    {
        if (root == 0 || hwnd == 0)
        {
            return 0;
        }

        var current = hwnd;
        while (current != 0)
        {
            var parent = NativeMethods.GetParent(current);
            if (parent == root)
            {
                return current;
            }

            current = parent;
        }

        return 0;
    }

    private static int GetZOrderIndex(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return -1;
        }

        var parent = NativeMethods.GetParent(hwnd);
        if (parent == 0)
        {
            var found = false;
            var topLevelIndex = 0;
            NativeMethods.EnumWindows((candidate, _) =>
            {
                if (candidate == hwnd)
                {
                    found = true;
                    return false;
                }

                topLevelIndex++;
                return true;
            }, 0);
            return found ? topLevelIndex : -1;
        }

        var first = hwnd;
        var previous = NativeMethods.GetWindow(first, NativeMethods.GwHwndPrev);
        var rewindGuard = 0;
        while (previous != 0 && rewindGuard++ < 10000)
        {
            first = previous;
            previous = NativeMethods.GetWindow(first, NativeMethods.GwHwndPrev);
        }

        var current = first;
        var index = 0;
        while (current != 0 && index < 10000)
        {
            if (current == hwnd)
            {
                return index;
            }

            current = NativeMethods.GetWindow(current, NativeMethods.GwHwndNext);
            index++;
        }

        return -1;
    }

    private static nint FindDescendant(nint parent, string className)
    {
        if (parent == 0)
        {
            return 0;
        }

        var found = NativeMethods.FindWindowEx(parent, 0, className, null);
        if (found != 0)
        {
            return found;
        }

        NativeMethods.EnumChildWindows(parent, (child, _) =>
        {
            if (NativeMethods.GetClassNameSafe(child).Equals(className, StringComparison.Ordinal))
            {
                found = child;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    private static bool IsVisibleAndUncloaked(nint hwnd) => hwnd != 0 && NativeMethods.IsWindowVisible(hwnd) && !IsCloaked(hwnd);

    private static bool IsCloaked(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return NativeMethods.DwmGetWindowAttribute(hwnd, DwmCloaked, out var value, sizeof(int)) == 0 && value != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool HasTaskbarAncestor(nint hwnd)
    {
        var current = hwnd;
        while (current != 0)
        {
            if (NativeMethods.GetClassNameSafe(current) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return true;
            }

            current = NativeMethods.GetParent(current);
        }

        return false;
    }

    private static bool IsRendererForeground(nint candidate, nint renderer)
    {
        if (candidate == 0 || renderer == 0)
        {
            return false;
        }

        return candidate == renderer || NativeMethods.GetAncestor(candidate, NativeMethods.GaRoot) == renderer;
    }

    private static bool IsDescendantOrSelf(nint candidate, nint root)
    {
        if (candidate == 0 || root == 0)
        {
            return false;
        }

        var current = candidate;
        while (current != 0)
        {
            if (current == root)
            {
                return true;
            }

            current = NativeMethods.GetParent(current);
        }

        return false;
    }

    private static int ReadIconCount(nint sysList) => unchecked((int)NativeMethods.SendMessage(sysList, NativeMethods.LvmGetItemCount, 0, 0).ToInt64());

    private static bool TryGetRect(nint hwnd, out NativeMethods.Rect rect) => NativeMethods.GetWindowRect(hwnd, out rect);

    private static RectD ToRectD(NativeMethods.Rect rect) => RectD.FromBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static RectD? TryGetMonitorRect(nint hwnd)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return null;
        }

        var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(), DeviceName = string.Empty };
        return NativeMethods.GetMonitorInfo(monitor, ref info) ? ToRectD(info.Monitor) : null;
    }

    private static bool Intersects(RectD left, RectD right) =>
        left.Width > 0 && left.Height > 0 && right.Width > 0 && right.Height > 0 &&
        left.X < right.Right && left.Right > right.X && left.Y < right.Bottom && left.Bottom > right.Y;

    private static int GetProcessId(nint hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return unchecked((int)pid);
    }

    private sealed record InputProbeResult(bool Available, long HitHwnd, string Diagnostic);
}

public sealed record DesktopShellCompositionProbeResult(
    RendererShellCompositionMetrics Metrics,
    IReadOnlyList<ShellWindowSnapshot> Windows,
    string Diagnostic);

public sealed record ShellWindowSnapshot(
    long Hwnd,
    string ClassName,
    string Text,
    int ProcessId,
    uint ThreadId,
    long ParentHwnd,
    long OwnerHwnd,
    long RootHwnd,
    long RootOwnerHwnd,
    long PreviousHwnd,
    long NextHwnd,
    long Style,
    long ExtendedStyle,
    bool Visible,
    bool Enabled,
    bool Cloaked,
    RectD? Rect,
    RectD? MonitorRect,
    int ZOrderIndex);
