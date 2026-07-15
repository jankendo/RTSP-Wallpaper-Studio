using System.Runtime.InteropServices;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Interop;

/// <summary>
/// Small, dependency-free screen probe for objective wallpaper verification.
/// It samples the actual desktop DC, so it is intentionally separate from LibVLC
/// and from the renderer's own state reports.
/// </summary>
public static class DesktopPixelProbe
{
    public static DesktopPixelSample Sample(RectD rect, int grid = 16)
    {
        var sampleCount = Math.Max(2, grid);
        var pixels = new List<uint>(sampleCount * sampleCount);
        var width = (int)Math.Clamp(Math.Round(rect.Width), 1, 4096);
        var height = (int)Math.Clamp(Math.Round(rect.Height), 1, 4096);
        var screenX = (int)Math.Round(rect.X);
        var screenY = (int)Math.Round(rect.Y);
        var screenDc = GetDC(0);
        if (screenDc == 0)
        {
            return new(0, 0, 0, $"GetDC failed Win32={Marshal.GetLastWin32Error()}", Array.Empty<uint>());
        }

        nint memoryDc = 0;
        nint bitmap = 0;
        nint oldBitmap = 0;
        nint bits = 0;
        var invalid = 0;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                return new(0, 0, 0, $"CreateCompatibleDC failed Win32={Marshal.GetLastWin32Error()}", Array.Empty<uint>());
            }

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb
                }
            };
            bitmap = CreateDibSection(screenDc, ref bitmapInfo, DibRgbColors, out bits, 0, 0);
            if (bitmap == 0 || bits == 0)
            {
                return new(0, 0, 0, $"CreateDIBSection failed Win32={Marshal.GetLastWin32Error()}", Array.Empty<uint>());
            }

            oldBitmap = SelectObject(memoryDc, bitmap);
            if (oldBitmap == 0 || !BitBlt(memoryDc, 0, 0, width, height, screenDc, screenX, screenY, SourceCopy | CaptureBlt))
            {
                return new(0, 0, 0, $"BitBlt failed Win32={Marshal.GetLastWin32Error()}", Array.Empty<uint>());
            }

            var raw = new byte[width * height * 4];
            Marshal.Copy(bits, raw, 0, raw.Length);
            for (var y = 0; y < sampleCount; y++)
            {
                var bitmapY = (int)Math.Round((height - 1) * y / (double)Math.Max(1, sampleCount - 1));
                for (var x = 0; x < sampleCount; x++)
                {
                    var bitmapX = (int)Math.Round((width - 1) * x / (double)Math.Max(1, sampleCount - 1));
                    var offset = (bitmapY * width + bitmapX) * 4;
                    pixels.Add((uint)(raw[offset] | (raw[offset + 1] << 8) | (raw[offset + 2] << 16)));
                }
            }
        }
        finally
        {
            if (oldBitmap != 0 && memoryDc != 0)
            {
                SelectObject(memoryDc, oldBitmap);
            }
            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }
            if (memoryDc != 0)
            {
                DeleteDc(memoryDc);
            }
            if (ReleaseDC(0, screenDc) == 0)
            {
                invalid++;
            }
        }

        var nonBlack = pixels.Count(x => (x & 0x00FFFFFF) != 0);
        var averageLuma = pixels.Count == 0
            ? 0
            : pixels.Average(x => (((x & 0xFF) * 299) + (((x >> 8) & 0xFF) * 587) + (((x >> 16) & 0xFF) * 114)) / 1000.0);
        return new(pixels.Count, nonBlack, averageLuma, invalid == 0 ? null : $"invalidSamples={invalid}", pixels.ToArray());
    }

    public static DesktopPixelDiff Compare(DesktopPixelSample first, DesktopPixelSample second)
    {
        var count = Math.Min(first.Pixels.Count, second.Pixels.Count);
        if (count == 0)
        {
            return new(0, 0, 0);
        }

        var changed = 0;
        var totalDelta = 0.0;
        for (var i = 0; i < count; i++)
        {
            var left = first.Pixels[i];
            var right = second.Pixels[i];
            var delta = Math.Abs((int)(left & 0xFF) - (int)(right & 0xFF)) +
                        Math.Abs((int)((left >> 8) & 0xFF) - (int)((right >> 8) & 0xFF)) +
                        Math.Abs((int)((left >> 16) & 0xFF) - (int)((right >> 16) & 0xFF));
            totalDelta += delta;
            if (delta >= 6)
            {
                changed++;
            }
        }

        return new(count, changed, totalDelta / count);
    }

    public static TestPatternMarkerProbe ProbeTestPatternMarkers(RectD rect)
    {
        var expected = new[]
        {
            RendererSelfTestPattern.RedMarker,
            RendererSelfTestPattern.GreenMarker,
            RendererSelfTestPattern.BlueMarker,
            RendererSelfTestPattern.YellowMarker
        };
        var positions = RendererSelfTestPattern.MarkerPositions;
        var colors = new List<uint>(positions.Count);
        var matched = 0;
        var screenDc = GetDC(0);
        if (screenDc == 0)
        {
            return new(false, 0, Array.Empty<uint>(), $"GetDC failed Win32={Marshal.GetLastWin32Error()}");
        }

        try
        {
            for (var i = 0; i < positions.Count; i++)
            {
                var centerX = rect.X + rect.Width * positions[i].Item1;
                var centerY = rect.Y + rect.Height * positions[i].Item2;
                var bestColor = 0u;
                var bestDistance = int.MaxValue;
                var markerMatched = false;
                // The Shell icon view is a transparent child in the normal
                // layout, but icon labels can cover the exact center sample.
                // Search a small area inside the deterministic marker instead
                // of treating one covered pixel as a failed presentation.
                for (var sampleY = -2; sampleY <= 2; sampleY++)
                {
                    for (var sampleX = -2; sampleX <= 2; sampleX++)
                    {
                        var x = (int)Math.Round(centerX + rect.Width * sampleX * 0.012);
                        var y = (int)Math.Round(centerY + rect.Height * sampleY * 0.012);
                        var colorRef = GetPixel(screenDc, x, y);
                        var rgb = (uint)(((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF));
                        var distance = ColorDistance(rgb, expected[i]);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestColor = rgb;
                        }

                        if (distance <= 75)
                        {
                            markerMatched = true;
                        }
                    }
                }

                colors.Add(bestColor);
                if (markerMatched)
                {
                    matched++;
                }
            }
        }
        finally
        {
            _ = ReleaseDC(0, screenDc);
        }

        return new(matched == expected.Length, matched, colors,
            $"matched={matched}/{expected.Length}; expected=red,green,blue,yellow");
    }

    public static TestPatternMarkerProbe ProbeWindowTestPatternMarkers(nint hwnd)
    {
        var expected = new[]
        {
            RendererSelfTestPattern.RedMarker,
            RendererSelfTestPattern.GreenMarker,
            RendererSelfTestPattern.BlueMarker,
            RendererSelfTestPattern.YellowMarker
        };
        var positions = RendererSelfTestPattern.MarkerPositions;
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.GetClientRect(hwnd, out var client))
        {
            return new(false, 0, Array.Empty<uint>(), "Renderer HWND/client rect is invalid");
        }

        var colors = new List<uint>(positions.Count);
        var matched = 0;
        var windowDc = GetDC(hwnd);
        if (windowDc == 0)
        {
            return new(false, 0, Array.Empty<uint>(), $"GetDC(hwnd) failed Win32={Marshal.GetLastWin32Error()}");
        }

        try
        {
            var width = Math.Max(1, client.Right - client.Left);
            var height = Math.Max(1, client.Bottom - client.Top);
            for (var i = 0; i < positions.Count; i++)
            {
                var x = (int)Math.Round((width - 1) * positions[i].X);
                var y = (int)Math.Round((height - 1) * positions[i].Y);
                var colorRef = GetPixel(windowDc, x, y);
                var rgb = (uint)(((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF));
                colors.Add(rgb);
                if (ColorDistance(rgb, expected[i]) <= 75)
                {
                    matched++;
                }
            }
        }
        finally
        {
            _ = ReleaseDC(hwnd, windowDc);
        }

        return new(matched == expected.Length, matched, colors,
            $"window matched={matched}/{expected.Length}; expected=red,green,blue,yellow");
    }

    private static int ColorDistance(uint left, uint right) =>
        Math.Abs((int)((left >> 16) & 0xFF) - (int)((right >> 16) & 0xFF)) +
        Math.Abs((int)((left >> 8) & 0xFF) - (int)((right >> 8) & 0xFF)) +
        Math.Abs((int)(left & 0xFF) - (int)(right & 0xFF));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint GetPixel(nint hdc, int x, int y);

    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad Color;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static extern nint CreateDibSection(nint hdc, ref BitmapInfo bitmapInfo, uint usage,
        out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint hdc, nint objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint objectHandle);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDc(nint hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint rasterOperation);
}

public sealed record DesktopPixelSample(
    int SampleCount,
    int NonBlackSampleCount,
    double AverageLuma,
    string? Diagnostic,
    IReadOnlyList<uint> Pixels)
{
    public bool HasNonBlackPixels => NonBlackSampleCount > 0;
}

public sealed record DesktopPixelDiff(int ComparedSamples, int ChangedSamples, double AverageAbsoluteRgbDelta)
{
    public bool HasMovement => ChangedSamples > 0 && AverageAbsoluteRgbDelta >= 6;
}

public sealed record TestPatternMarkerProbe(bool Detected, int MatchedMarkers, IReadOnlyList<uint> Colors, string Diagnostic);
