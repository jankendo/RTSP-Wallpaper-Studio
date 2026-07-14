using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Core.Services;

public sealed record VideoLayoutResult(RectD VideoRect, bool Cropped, double Scale);

public static class VideoLayoutCalculator
{
    public static VideoLayoutResult Calculate(
        int sourceWidth,
        int sourceHeight,
        RectD target,
        DisplayMode mode,
        double scale = 1)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || target.Width <= 0 || target.Height <= 0)
        {
            return new VideoLayoutResult(target, false, 1);
        }

        if (mode == DisplayMode.Stretch)
        {
            return new VideoLayoutResult(target, false, target.Width / sourceWidth);
        }

        var sourceAspect = (double)sourceWidth / sourceHeight;
        var targetAspect = target.Width / target.Height;
        var fitScale = mode is DisplayMode.Center or DisplayMode.OneToOne
            ? (mode == DisplayMode.OneToOne ? 1 : Math.Min(target.Width / sourceWidth, target.Height / sourceHeight) * scale)
            : targetAspect > sourceAspect
                ? target.Height / sourceHeight
                : target.Width / sourceWidth;
        var finalScale = mode == DisplayMode.Fill
            ? (targetAspect > sourceAspect ? target.Width / sourceWidth : target.Height / sourceHeight)
            : fitScale;
        var width = sourceWidth * finalScale;
        var height = sourceHeight * finalScale;
        var rect = new RectD(target.X + ((target.Width - width) / 2), target.Y + ((target.Height - height) / 2), width, height);
        return new VideoLayoutResult(rect, mode == DisplayMode.Fill && (width > target.Width || height > target.Height), finalScale);
    }
}
