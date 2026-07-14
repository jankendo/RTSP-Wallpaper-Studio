using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.Interop;

public sealed class DesktopMonitorProvider
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (nint handle, nint hdc, ref NativeMethods.Rect callbackRect, nint data) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty
            };

            if (!NativeMethods.GetMonitorInfo(handle, ref info))
            {
                return true;
            }

            var width = info.Monitor.Right - info.Monitor.Left;
            var height = info.Monitor.Bottom - info.Monitor.Top;
            var sourceName = info.DeviceName.TrimEnd('\0');
            monitors.Add(new MonitorInfo
            {
                PersistentId = MonitorIdentity.Create(sourceName, null, width, height),
                SourceDeviceName = sourceName,
                FriendlyName = sourceName,
                Bounds = ToRect(info.Monitor),
                WorkArea = ToRect(info.Work),
                Width = width,
                Height = height,
                IsPrimary = (info.Flags & 1) == 1,
                ScalePercentage = 100
            });
            return true;
        };

        NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        return monitors.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Bounds.X).ThenBy(x => x.Bounds.Y).ToArray();
    }

    private static RectD ToRect(NativeMethods.Rect rect) =>
        RectD.FromBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
