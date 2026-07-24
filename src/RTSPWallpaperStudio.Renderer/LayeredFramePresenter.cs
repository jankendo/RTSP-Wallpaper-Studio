using System.Runtime.InteropServices;

namespace RTSPWallpaperStudio.Renderer;

internal static class LayeredFramePresenter
{
    public static int Present(nint hwnd, byte[] bgra, int sourceWidth, int sourceHeight, string diagnosticText)
    {
        if (hwnd == 0 || bgra.Length < sourceWidth * sourceHeight * 4 ||
            !RendererWin32.GetClientRect(hwnd, out var client))
        {
            return 0;
        }

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var screenDc = RendererWin32.GetDC(0);
        var memoryDc = screenDc == 0 ? 0 : RendererWin32.CreateCompatibleDC(screenDc);
        nint bitmap = 0;
        nint oldBitmap = 0;
        try
        {
            if (screenDc == 0 || memoryDc == 0)
            {
                return 0;
            }

            var destinationInfo = CreateBitmapInfo(width, height);
            bitmap = RendererWin32.CreateDIBSection(screenDc, ref destinationInfo,
                RendererWin32.DibRgbColors, out var destinationBits, 0, 0);
            if (bitmap == 0 || destinationBits == 0)
            {
                return 0;
            }

            oldBitmap = RendererWin32.SelectObject(memoryDc, bitmap);
            var sourceInfo = CreateBitmapInfo(sourceWidth, sourceHeight);
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var stretched = RendererWin32.StretchDIBits(memoryDc, 0, 0, width, height,
                    0, 0, sourceWidth, sourceHeight, handle.AddrOfPinnedObject(), ref sourceInfo,
                    RendererWin32.DibRgbColors, RendererWin32.SrcCopy);
                if (stretched <= 0)
                {
                    return 0;
                }
            }
            finally
            {
                handle.Free();
            }

            if (!string.IsNullOrWhiteSpace(diagnosticText))
            {
                DrawDiagnostic(memoryDc, width, height, diagnosticText);
            }
            ForceOpaqueAlpha(destinationBits, width, height);
            RevealDesktopIcons(destinationBits, width, height, DesktopIconRegionProvider.GetRelativeRects(hwnd));
            var destinationSize = new RendererWin32.Size
            {
                Width = width,
                Height = height
            };
            var sourcePoint = new RendererWin32.Point();
            var blend = new RendererWin32.BlendFunction
            {
                BlendOp = RendererWin32.AcSrcOver,
                SourceConstantAlpha = byte.MaxValue,
                AlphaFormat = RendererWin32.AcSrcAlpha
            };
            return RendererWin32.UpdateLayeredWindow(hwnd, screenDc, 0, ref destinationSize, memoryDc,
                ref sourcePoint, 0, ref blend, RendererWin32.UlwAlpha) ? height : 0;
        }
        finally
        {
            if (oldBitmap != 0 && memoryDc != 0)
            {
                RendererWin32.SelectObject(memoryDc, oldBitmap);
            }
            if (bitmap != 0)
            {
                RendererWin32.DeleteObject(bitmap);
            }
            if (memoryDc != 0)
            {
                RendererWin32.DeleteDC(memoryDc);
            }
            if (screenDc != 0)
            {
                _ = RendererWin32.ReleaseDC(0, screenDc);
            }
        }
    }

    private static unsafe void ForceOpaqueAlpha(nint bits, int width, int height)
    {
        var pixelCount = checked(width * height);
        var bytes = (byte*)bits;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            bytes[(pixel * 4) + 3] = byte.MaxValue;
        }
    }

    private static unsafe void RevealDesktopIcons(nint bits, int width, int height,
        IReadOnlyList<DesktopIconRegionProvider.IconRect> iconRects)
    {
        var bytes = (byte*)bits;
        foreach (var rect in iconRects)
        {
            var left = Math.Clamp(rect.Left, 0, width);
            var top = Math.Clamp(rect.Top, 0, height);
            var right = Math.Clamp(rect.Right, 0, width);
            var bottom = Math.Clamp(rect.Bottom, 0, height);
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var alphaOffset = ((y * width + x) * 4) + 3;
                    bytes[alphaOffset] = Math.Min(bytes[alphaOffset], rect.Alpha);
                }
            }
        }
    }

    private static RendererWin32.BitmapInfo CreateBitmapInfo(int width, int height) => new()
    {
        Header = new RendererWin32.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<RendererWin32.BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = RendererWin32.BiRgb,
            SizeImage = (uint)(width * height * 4)
        }
    };

    private static void DrawDiagnostic(nint hdc, int width, int height, string text)
    {
        var font = RendererWin32.CreateFont(-24, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var oldFont = font == 0 ? 0 : RendererWin32.SelectObject(hdc, font);
        _ = RendererWin32.SetBkMode(hdc, RendererWin32.Transparent);
        _ = RendererWin32.SetTextColor(hdc, 0x00FFFFFF);
        var rect = new RendererWin32.Rect { Left = 28, Top = 28, Right = Math.Max(300, width - 28), Bottom = Math.Min(height, 180) };
        _ = RendererWin32.DrawText(hdc, text, text.Length, ref rect, 0);
        if (oldFont != 0) RendererWin32.SelectObject(hdc, oldFont);
        if (font != 0) RendererWin32.DeleteObject(font);
    }
}
