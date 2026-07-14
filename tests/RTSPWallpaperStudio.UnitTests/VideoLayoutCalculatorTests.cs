using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.UnitTests;

public sealed class VideoLayoutCalculatorTests
{
    [Fact]
    public void Fit_PreservesAspectRatioAndAddsLetterbox()
    {
        var result = VideoLayoutCalculator.Calculate(1920, 1080, new RectD(0, 0, 1000, 1000), DisplayMode.Fit);

        Assert.Equal(1000, result.VideoRect.Width, 6);
        Assert.Equal(562.5, result.VideoRect.Height, 6);
        Assert.False(result.Cropped);
    }

    [Fact]
    public void Fill_CropsWhenAspectRatioDiffers()
    {
        var result = VideoLayoutCalculator.Calculate(1920, 1080, new RectD(-1920, 0, 1920, 1080), DisplayMode.Fill);

        Assert.False(result.Cropped);
        Assert.Equal(-1920, result.VideoRect.X);
    }

    [Fact]
    public void Span_UnionSupportsNegativeVirtualCoordinates()
    {
        var union = RectD.Union([new RectD(-1920, 0, 1920, 1080), new RectD(0, 0, 2560, 1440)]);

        Assert.Equal(-1920, union.X);
        Assert.Equal(4480, union.Width);
        Assert.Equal(1440, union.Height);
    }
}
