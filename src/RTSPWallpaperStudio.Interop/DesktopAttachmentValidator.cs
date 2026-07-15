using System.Runtime.InteropServices;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

public static class DesktopAttachmentValidator
{
    public static bool Validate(nint rendererHwnd, DesktopHostDiscoveryResult discovery, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (rendererHwnd == 0 || !NativeMethods.IsWindow(rendererHwnd))
        {
            diagnostic = "Renderer HWNDが無効です。";
            return false;
        }

        if (discovery.HostHwnd == 0 || !NativeMethods.IsWindow(discovery.HostHwnd))
        {
            diagnostic = "期待するDesktop host HWNDが無効です。";
            return false;
        }

        var actualParent = NativeMethods.GetParent(rendererHwnd);
        var raisedDesktop = discovery.Strategy == DesktopLayoutStrategy.RaisedDesktop;
        var expectedParent = raisedDesktop ? NativeMethods.HwndDesktop : discovery.HostHwnd;
        if (actualParent != expectedParent)
        {
            diagnostic = $"実際の親HWNDが不一致です。actual=0x{actualParent.ToInt64():X} expected=0x{expectedParent.ToInt64():X}";
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(rendererHwnd, NativeMethods.GwlStyle).ToInt64();
        if (raisedDesktop
            ? (style & NativeMethods.WsPopup) == 0 || (style & NativeMethods.WsChild) != 0
            : (style & NativeMethods.WsChild) == 0 || (style & NativeMethods.WsPopup) != 0)
        {
            diagnostic = $"Renderer styleが不正です。strategy={discovery.Strategy}; style=0x{style:X}";
            return false;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(rendererHwnd, NativeMethods.GwlexStyle).ToInt64();
        var forbiddenTopLevelBits = NativeMethods.WsExTopmost | NativeMethods.WsExAppWindow;
        if ((extendedStyle & forbiddenTopLevelBits) != 0 ||
            (extendedStyle & (NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate)) !=
            (NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate))
        {
            diagnostic = $"Renderer拡張スタイルが安全契約に違反しています。exStyle=0x{extendedStyle:X}; topmost/appwindowは禁止、toolwindow/noactivateが必須です。";
            return false;
        }

        if (raisedDesktop && NativeMethods.GetWindow(rendererHwnd, NativeMethods.GwHwndOwner) != 0)
        {
            diagnostic = "Raised DesktopのトップレベルRendererにOwnerが設定されています。";
            return false;
        }

        var parentClass = NativeMethods.GetClassNameSafe(actualParent);
        if (!raisedDesktop &&
            (!parentClass.Equals("WorkerW", StringComparison.Ordinal) ||
             NativeMethods.GetParent(actualParent) != discovery.ProgmanHwnd ||
             !NativeMethods.IsWindowVisible(actualParent)))
        {
            diagnostic = $"Legacy WorkerWの親契約が不正です。parent=0x{actualParent.ToInt64():X}; class={parentClass}; progman=0x{discovery.ProgmanHwnd.ToInt64():X}";
            return false;
        }
        if (DesktopHostDiscovery.IsForbiddenHost(actualParent) ||
            parentClass.Equals("SHELLDLL_DefView", StringComparison.Ordinal) ||
            parentClass.Equals("SysListView32", StringComparison.Ordinal))
        {
            diagnostic = $"禁止されたShellウィンドウへ親子付けされています。class={parentClass}";
            return false;
        }

        var ancestor = NativeMethods.GetAncestor(rendererHwnd, NativeMethods.GaRoot);
        var rootClass = NativeMethods.GetClassNameSafe(ancestor);
        if (rootClass.Equals("Shell_TrayWnd", StringComparison.Ordinal) || rootClass.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal))
        {
            diagnostic = "Rendererがタスクバー階層に入っています。";
            return false;
        }

        return true;
    }

    public static bool ValidateRect(nint rendererHwnd, RectD monitor, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (!NativeMethods.GetWindowRect(rendererHwnd, out var rect))
        {
            diagnostic = $"Renderer矩形を取得できません。Win32={Marshal.GetLastWin32Error()}";
            return false;
        }

        const int tolerance = 2;
        var inside = rect.Left >= monitor.X - tolerance && rect.Top >= monitor.Y - tolerance &&
                     rect.Right <= monitor.Right + tolerance && rect.Bottom <= monitor.Bottom + tolerance &&
                     rect.Width > 0 && rect.Height > 0;
        if (!inside)
        {
            diagnostic = $"Renderer矩形が対象モニター外です。renderer={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} monitor={monitor}";
        }

        return inside;
    }
}
