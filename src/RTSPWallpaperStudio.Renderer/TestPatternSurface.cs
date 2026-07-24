using System.Runtime.InteropServices;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Renderer;

/// <summary>
/// A deterministic animated surface rendered by the same HWND and GDI path as
/// decoded RTSP frames. It is deliberately independent of LibVLC so a failure
/// here proves the desktop/window/presentation path is broken, not the camera.
/// </summary>
internal sealed class TestPatternSurface : IDisposable
{
    private const int Width = 960;
    private const int Height = 540;
    private readonly Action _invalidate;
    private readonly byte[] _pixels = new byte[Width * Height * 4];
    private readonly Timer _timer;
    private long _frameNumber;
    private long _tickCount;
    private ulong _lastChecksum;
    private bool _disposed;

    public TestPatternSurface(Action invalidate)
    {
        _invalidate = invalidate;
        _timer = new Timer(static state => ((TestPatternSurface)state!).Tick(), this, 0, 100);
    }

    public long FrameNumber => Interlocked.Read(ref _frameNumber);
    public ulong LastChecksum => _lastChecksum;
    public bool HasRenderedFrame => FrameNumber > 0;
    public long TickCount => Interlocked.Read(ref _tickCount);

    public int Paint(nint hdc, int destinationWidth, int destinationHeight, nint hwnd, int processId)
    {
        if (_disposed || hdc == 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            return 0;
        }

        var frame = Interlocked.Increment(ref _frameNumber);
        Render(frame);
        var bitmapInfo = new RendererWin32.BitmapInfo
        {
            Header = new RendererWin32.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<RendererWin32.BitmapInfoHeader>(),
                Width = Width,
                Height = -Height,
                Planes = 1,
                BitCount = 32,
                Compression = RendererWin32.BiRgb,
                SizeImage = (uint)_pixels.Length
            }
        };

        var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            var result = RendererWin32.StretchDIBits(hdc, 0, 0, destinationWidth, destinationHeight,
                0, 0, Width, Height, handle.AddrOfPinnedObject(), ref bitmapInfo,
                RendererWin32.DibRgbColors, RendererWin32.SrcCopy);
            if (result > 0)
            {
                DrawDiagnosticText(hdc, destinationWidth, destinationHeight, frame, hwnd, processId);
            }

            return result;
        }
        finally
        {
            handle.Free();
        }
    }

    public bool TryCopyNextFrame(out byte[] pixels, out int width, out int height, out ulong checksum)
    {
        pixels = [];
        width = Width;
        height = Height;
        checksum = 0;
        if (_disposed)
        {
            return false;
        }

        var frame = Interlocked.Increment(ref _frameNumber);
        Render(frame);
        pixels = (byte[])_pixels.Clone();
        checksum = _lastChecksum;
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private void Tick()
    {
        Interlocked.Increment(ref _tickCount);
        if (!_disposed)
        {
            _invalidate();
        }
    }

    private void Render(long frame)
    {
        var phase = (int)(frame % 96);
        var pulse = (int)(frame % 20) * 8;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var checker = ((x / 48) + (y / 48)) % 2 == 0;
                var red = Math.Min(245, (checker ? 22 : 10) + pulse);
                var green = Math.Min(245, (checker ? 42 : 20) + pulse / 2);
                var blue = Math.Min(245, (checker ? 70 : 36) + pulse / 3);
                var moving = x >= phase * 6 && x < phase * 6 + 320 && y >= 120 && y < 420;
                if (moving)
                {
                    red = 225;
                    green = 155;
                    blue = 32;
                }

                SetPixel(x, y, (uint)((red << 16) | (green << 8) | blue));
            }
        }

        FillMarkerCentered(RendererSelfTestPattern.MarkerPositions[0], RendererSelfTestPattern.RedMarker);
        FillMarkerCentered(RendererSelfTestPattern.MarkerPositions[1], RendererSelfTestPattern.GreenMarker);
        FillMarkerCentered(RendererSelfTestPattern.MarkerPositions[2], RendererSelfTestPattern.BlueMarker);
        FillMarkerCentered(RendererSelfTestPattern.MarkerPositions[3], RendererSelfTestPattern.YellowMarker);
        _lastChecksum = ComputeChecksum(_pixels);
    }

    private void FillMarker(int left, int top, int right, int bottom, uint rgb)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                SetPixel(x, y, rgb);
            }
        }
    }

    private void FillMarkerCentered((double X, double Y) position, uint rgb)
    {
        const int size = 72;
        var centerX = (int)Math.Round(Width * position.X);
        var centerY = (int)Math.Round(Height * position.Y);
        FillMarker(centerX - size / 2, centerY - size / 2, centerX + size / 2, centerY + size / 2, rgb);
    }

    private void SetPixel(int x, int y, uint rgb)
    {
        var offset = (y * Width + x) * 4;
        _pixels[offset] = (byte)(rgb & 0xFF);
        _pixels[offset + 1] = (byte)((rgb >> 8) & 0xFF);
        _pixels[offset + 2] = (byte)((rgb >> 16) & 0xFF);
        _pixels[offset + 3] = 0xFF;
    }

    private static ulong ComputeChecksum(byte[] bytes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        for (var i = 0; i < bytes.Length; i += 97)
        {
            hash ^= bytes[i];
            hash *= prime;
        }

        return hash;
    }

    private static void DrawDiagnosticText(nint hdc, int width, int height, long frame, nint hwnd, int processId)
    {
        var font = RendererWin32.CreateFont(-24, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var oldFont = font == 0 ? 0 : RendererWin32.SelectObject(hdc, font);
        _ = RendererWin32.SetBkMode(hdc, RendererWin32.Transparent);
        _ = RendererWin32.SetTextColor(hdc, 0x00FFFFFF);
        var rect = new RendererWin32.Rect { Left = 28, Top = 88, Right = Math.Max(300, width - 28), Bottom = Math.Min(height, 190) };
        var text = $"RTSP WALLPAPER TEST\nframe={frame}  pid={processId}  hwnd=0x{hwnd.ToInt64():X}";
        _ = RendererWin32.DrawText(hdc, text, text.Length, ref rect, 0x00000000);
        if (oldFont != 0)
        {
            RendererWin32.SelectObject(hdc, oldFont);
        }
        if (font != 0)
        {
            RendererWin32.DeleteObject(font);
        }
    }
}
