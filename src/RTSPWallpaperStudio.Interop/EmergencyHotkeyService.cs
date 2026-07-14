using System.Runtime.InteropServices;

namespace RTSPWallpaperStudio.Interop;

public sealed class EmergencyHotkeyService
{
    public const int MessageId = 0x0312;
    public const int HotkeyId = 0x5254;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyF12 = 0x7B;
    private nint _window;

    public bool Register(nint window)
    {
        _window = window;
        return RegisterHotKey(window, HotkeyId, ModControl | ModAlt | ModShift | ModNoRepeat, VirtualKeyF12);
    }

    public bool IsEmergencyMessage(int message, nint wParam) => message == MessageId && wParam.ToInt64() == HotkeyId;

    public void Unregister()
    {
        if (_window != 0)
        {
            UnregisterHotKey(_window, HotkeyId);
            _window = 0;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
