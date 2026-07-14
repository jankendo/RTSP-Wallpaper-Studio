using System.Runtime.InteropServices;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Performs a hidden, verifiable attachment. A renderer is never shown until every invariant
/// has passed; failures roll back or destroy the window so a top-level full-screen window cannot remain.
/// </summary>
public sealed class DesktopAttachmentTransaction
{
    private readonly DesktopHostDiscoveryResult _discovery;
    private readonly MonitorInfo _monitor;

    public DesktopAttachmentTransaction(DesktopHostDiscoveryResult discovery, MonitorInfo monitor)
    {
        _discovery = discovery;
        _monitor = monitor;
    }

    public DesktopAttachResult Attach(nint rendererHwnd)
    {
        if (rendererHwnd == 0 || !NativeMethods.IsWindow(rendererHwnd))
        {
            return Failure(RendererErrorCodes.WallpaperSetParentFailed, "Rendererウィンドウを検証できません。", "Renderer HWNDが無効です。");
        }

        if (!_discovery.Success || _discovery.HostHwnd == 0 || !NativeMethods.IsWindow(_discovery.HostHwnd))
        {
            return Failure(RendererErrorCodes.DesktopHostNotFound, "安全な壁紙ホストがありません。", _discovery.Diagnostic);
        }

        var snapshot = Capture(rendererHwnd);
        NativeMethods.ShowWindow(rendererHwnd, NativeMethods.SwHide);

        try
        {
            var style = NativeMethods.GetWindowLongPtr(rendererHwnd, NativeMethods.GwlStyle).ToInt64();
            var newStyle = (style | NativeMethods.WsChild | NativeMethods.WsClipChildren | NativeMethods.WsClipSiblings) & ~NativeMethods.WsPopup;
            if (!SetWindowStyle(rendererHwnd, NativeMethods.GwlStyle, newStyle, out var styleError))
            {
                return FailAndRollback(rendererHwnd, snapshot, RendererErrorCodes.WallpaperStyleUpdateFailed,
                    "Rendererのウィンドウスタイル変更に失敗しました。", $"SetWindowLongPtr(style) Win32={styleError}");
            }

            var exStyle = NativeMethods.GetWindowLongPtr(rendererHwnd, NativeMethods.GwlexStyle).ToInt64();
            var newExStyle = exStyle | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
            if (!SetWindowStyle(rendererHwnd, NativeMethods.GwlexStyle, newExStyle, out var exStyleError))
            {
                return FailAndRollback(rendererHwnd, snapshot, RendererErrorCodes.WallpaperStyleUpdateFailed,
                    "Rendererの拡張ウィンドウスタイル変更に失敗しました。", $"SetWindowLongPtr(exStyle) Win32={exStyleError}");
            }

            var frameFlags = NativeMethods.SetWindowPosNoActivate | NativeMethods.SetWindowPosFrameChanged | NativeMethods.SetWindowPosNoSendChanging;
            if (!NativeMethods.SetWindowPos(rendererHwnd, 0, 0, 0, 0, 0,
                    frameFlags | NativeMethods.SetWindowPosNoMove | NativeMethods.SetWindowPosNoSize | NativeMethods.SetWindowPosNoZOrder))
            {
                return FailAndRollback(rendererHwnd, snapshot, RendererErrorCodes.WallpaperStyleUpdateFailed,
                    "Rendererのフレームスタイル反映に失敗しました。", $"SetWindowPos(frame) Win32={Marshal.GetLastWin32Error()}");
            }

            NativeMethods.SetLastError(0);
            NativeMethods.SetParent(rendererHwnd, _discovery.HostHwnd);
            var setParentError = Marshal.GetLastPInvokeError();
            var actualParent = NativeMethods.GetAncestor(rendererHwnd, NativeMethods.GaParent);
            if (actualParent != _discovery.HostHwnd)
            {
                var code = setParentError == 0 ? RendererErrorCodes.WallpaperParentMismatch : RendererErrorCodes.WallpaperSetParentFailed;
                return FailAndRollback(rendererHwnd, snapshot, code,
                    "Rendererを安全な壁紙ホストへ配置できませんでした.",
                    $"SetParent Win32={setParentError}; actualParent=0x{actualParent.ToInt64():X}; expectedParent=0x{_discovery.HostHwnd.ToInt64():X}");
            }

            if (!TryMapMonitorToParent(_discovery.HostHwnd, _monitor.Bounds, out var localTopLeft, out var mappingDiagnostic))
            {
                return FailAndRollback(rendererHwnd, snapshot, RendererErrorCodes.WallpaperCoordinateMappingFailed,
                    "ディスプレイ座標を壁紙ホスト座標へ変換できませんでした。", mappingDiagnostic);
            }

            var insertAfter = _discovery.Strategy == DesktopLayoutStrategy.RaisedDesktop ? _discovery.ShellViewHwnd : 0;
            var positionFlags = NativeMethods.SetWindowPosNoActivate | NativeMethods.SetWindowPosFrameChanged | NativeMethods.SetWindowPosNoSendChanging;
            if (_discovery.Strategy == DesktopLayoutStrategy.LegacyWorkerW)
            {
                positionFlags |= NativeMethods.SetWindowPosNoZOrder | NativeMethods.SetWindowPosNoOwnerZOrder;
            }

            if (!NativeMethods.SetWindowPos(rendererHwnd, insertAfter, localTopLeft.X, localTopLeft.Y,
                    (int)_monitor.Bounds.Width, (int)_monitor.Bounds.Height, positionFlags))
            {
                return FailAndRollback(rendererHwnd, snapshot, RendererErrorCodes.WallpaperRectValidationFailed,
                    "壁紙ウィンドウの配置に失敗しました。", $"SetWindowPos Win32={Marshal.GetLastWin32Error()}");
            }

            var validationOk = DesktopAttachmentValidator.Validate(rendererHwnd, _discovery, out var validationDiagnostic);
            var rectOk = DesktopAttachmentValidator.ValidateRect(rendererHwnd, _monitor.Bounds, out var rectDiagnostic);
            if (!validationOk || !rectOk)
            {
                var diagnostic = string.IsNullOrWhiteSpace(validationDiagnostic) ? rectDiagnostic : validationDiagnostic;
                return FailAndRollback(rendererHwnd, snapshot, RendererErrorCodes.WallpaperRectValidationFailed,
                    "壁紙ウィンドウの親子関係または矩形検証に失敗しました。", diagnostic);
            }

            return new DesktopAttachResult(true, string.Empty, "壁紙ホストへの配置を検証しました。",
                $"strategy={_discovery.Strategy}; host=0x{_discovery.HostHwnd.ToInt64():X}; shellView=0x{_discovery.ShellViewHwnd.ToInt64():X}", snapshot, _discovery);
        }
        catch (Exception ex)
        {
            return FailAndRollback(rendererHwnd, snapshot, RendererErrorCodes.WallpaperSetParentFailed,
                "壁紙配置中に予期しないエラーが発生しました。", ex.ToString());
        }
    }

    public static WindowSnapshot Capture(nint hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out var rect);
        return new WindowSnapshot(
            NativeMethods.GetAncestor(hwnd, NativeMethods.GaParent),
            NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64(),
            NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlexStyle).ToInt64(),
            ToRectD(rect),
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndPrev),
            NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndNext));
    }

    public static bool Rollback(nint hwnd, WindowSnapshot snapshot, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            diagnostic = "Rollback対象のRenderer HWNDが無効です。";
            return false;
        }

        NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
        NativeMethods.SetLastError(0);
        NativeMethods.SetParent(hwnd, snapshot.ParentHwnd);
        var parentError = Marshal.GetLastPInvokeError();
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GaParent) != snapshot.ParentHwnd && parentError != 0)
        {
            diagnostic = $"元の親へ復元できません。Win32={parentError}";
            NativeMethods.DestroyWindow(hwnd);
            return false;
        }

        var styleOk = SetWindowStyle(hwnd, NativeMethods.GwlStyle, snapshot.Style, out var styleError);
        var exStyleOk = SetWindowStyle(hwnd, NativeMethods.GwlexStyle, snapshot.ExtendedStyle, out var exStyleError);
        if (!styleOk || !exStyleOk)
        {
            diagnostic = $"元のウィンドウスタイルへ復元できません。style={styleError}; exStyle={exStyleError}";
            NativeMethods.DestroyWindow(hwnd);
            return false;
        }

        if (!NativeMethods.SetWindowPos(hwnd, snapshot.PreviousSibling, (int)snapshot.ScreenRect.X, (int)snapshot.ScreenRect.Y,
                (int)snapshot.ScreenRect.Width, (int)snapshot.ScreenRect.Height,
                NativeMethods.SetWindowPosNoActivate | NativeMethods.SetWindowPosFrameChanged | NativeMethods.SetWindowPosNoZOrder))
        {
            diagnostic = $"元の矩形へ復元できません。Win32={Marshal.GetLastWin32Error()}";
            NativeMethods.DestroyWindow(hwnd);
            return false;
        }

        if (snapshot.IsVisible)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwShow);
        }

        return true;
    }

    private DesktopAttachResult FailAndRollback(nint hwnd, WindowSnapshot snapshot, string code, string message, string details)
    {
        if (!Rollback(hwnd, snapshot, out var rollbackDiagnostic))
        {
            details += $"; rollback={rollbackDiagnostic}";
        }

        return Failure(code, message, details, snapshot);
    }

    private static bool SetWindowStyle(nint hwnd, int index, long style, out int error)
    {
        NativeMethods.SetLastError(0);
        NativeMethods.SetWindowLongPtr(hwnd, index, new nint(style));
        error = Marshal.GetLastPInvokeError();
        var current = NativeMethods.GetWindowLongPtr(hwnd, index).ToInt64();
        return error == 0 && current == style;
    }

    private static bool TryMapMonitorToParent(nint parent, RectD monitor, out NativeMethods.Point point, out string diagnostic)
    {
        point = new NativeMethods.Point { X = (int)monitor.X, Y = (int)monitor.Y };
        diagnostic = string.Empty;
        NativeMethods.SetLastError(0);
        var mappedPoints = NativeMethods.MapWindowPoints(NativeMethods.HwndDesktop, parent, ref point, 1);
        _ = mappedPoints;
        var error = Marshal.GetLastPInvokeError();
        if (error != 0)
        {
            diagnostic = $"MapWindowPoints Win32={error}; screen={monitor.X},{monitor.Y}; parent=0x{parent.ToInt64():X}";
            return false;
        }

        return true;
    }

    private static RectD ToRectD(NativeMethods.Rect rect) => RectD.FromBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static DesktopAttachResult Failure(string code, string message, string details, WindowSnapshot? snapshot = null) =>
        new(false, code, message, details, snapshot);
}
