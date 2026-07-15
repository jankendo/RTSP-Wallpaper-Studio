using System.Runtime.InteropServices;
using System.Text;

namespace RTSPWallpaperStudio.Renderer;

internal static class RendererWin32
{
    internal const int GwlStyle = -16;
    internal const int GwlexStyle = -20;
    internal const int SwHide = 0;
    internal const int SwShow = 5;
    internal const int SwShowNoActivate = 4;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpFrameChanged = 0x0020;
    internal const long WsPopup = 0x80000000L;
    internal const long WsChild = 0x40000000L;
    internal const long WsClipChildren = 0x02000000L;
    internal const long WsClipSiblings = 0x04000000L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const long WsExLayered = 0x00080000L;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmClose = 0x0010;
    internal const uint WmEraseBkgnd = 0x0014;
    internal const uint WmPaint = 0x000F;
    internal const uint WmNcDestroy = 0x0082;
    internal const uint WmTimer = 0x0113;
    internal const uint WmRenderTick = 0x8001;
    internal const uint DibRgbColors = 0;
    internal const uint SrcCopy = 0x00CC0020;
    internal const uint BiRgb = 0;
    internal const int Transparent = 1;
    internal const byte AcSrcOver = 0;
    internal const byte AcSrcAlpha = 1;
    internal const uint UlwAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProcDelegate WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Hwnd;
        public uint MessageId;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect PaintRect;
        public int Restore;
        public int IncUpdate;
        public nint Reserved0;
        public nint Reserved1;
        public nint Reserved2;
        public nint Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WndProcDelegate(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(int extendedStyle, string className, string? windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out Message message, nint hwnd, uint minMessage, uint maxMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint child, nint parent);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint BeginPaint(nint hwnd, out PaintStruct paint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(nint hwnd, ref PaintStruct paint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nuint SetTimer(nint hwnd, nuint timerId, uint milliseconds, nint timerCallback);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint hwnd, nuint timerId);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int StretchDIBits(nint hdc, int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight, nint bits, ref BitmapInfo bitmapInfo,
        uint usage, uint rasterOperation);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, nint destinationPoint,
        ref Size size, nint sourceDc, ref Point sourcePoint, uint colorKey,
        ref BlendFunction blend, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    internal static extern nint CreateDIBSection(nint hdc, ref BitmapInfo bitmapInfo, uint usage,
        out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateSolidBrush(uint color);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int FillRect(nint hdc, ref Rect rect, nint brush);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(nint hdc, nint objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint objectHandle);

    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int DrawText(nint hdc, string text, int characterCount, ref Rect rect, uint format);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void SetLastError(uint errorCode);
}
