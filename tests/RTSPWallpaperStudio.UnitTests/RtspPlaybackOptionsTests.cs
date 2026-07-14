using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.UnitTests;

public sealed class RtspPlaybackOptionsTests
{
    [Fact]
    public void TcpAddsTcpOnlyAndClampsCache()
    {
        var options = RtspPlaybackOptionsFactory.CreateMediaOptions(new RtspPlaybackOptions(
            "rtsp://127.0.0.1:8554/test", "user", "p:a@ss", TransportMode.Tcp, 1,
            HardwareDecodeMode.Disabled));

        Assert.Contains(":rtsp-tcp", options);
        Assert.Contains(":network-caching=50", options);
        Assert.Contains(":avcodec-hw=none", options);
    }

    [Theory]
    [InlineData(TransportMode.Udp)]
    [InlineData(TransportMode.Automatic)]
    public void UdpAndAutomaticDoNotForceTcp(TransportMode transport)
    {
        var options = RtspPlaybackOptionsFactory.CreateMediaOptions(new RtspPlaybackOptions(
            "rtsp://127.0.0.1:8554/test", null, null, transport, 300, HardwareDecodeMode.Automatic));

        Assert.DoesNotContain(":rtsp-tcp", options);
    }

    [Fact]
    public void LocationBuilderPreservesSpecialPasswordForLibVlcOnly()
    {
        var location = RtspLocationBuilder.Build("rtsp://127.0.0.1:8554/test", "camera", "p:a@ss/日本語");

        Assert.Contains("camera", location);
        Assert.DoesNotContain("p:a@ss/日本語", location);
    }

    [Fact]
    public void NavigationPaletteMeetsNormalTextContrast()
    {
        Assert.True(ColorContrastCalculator.ContrastRatio("#FFFFFF", "#22304A") >= 4.5);
        Assert.True(ColorContrastCalculator.ContrastRatio("#FFFFFF", "#3C65A3") >= 4.5);
        Assert.True(ColorContrastCalculator.ContrastRatio("#172235", "#F6C453") >= 3.0);
    }
}
