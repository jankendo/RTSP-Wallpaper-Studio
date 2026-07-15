using System.Text.Json;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.UnitTests;

public sealed class RendererVerificationContractTests
{
    [Fact]
    public void RendererStartOptions_PreservesRenderTestPatternInJsonContract()
    {
        var options = new RendererStartOptions(
            "rtsp://127.0.0.1:8554/self-test", null, null, TransportMode.Tcp, 300,
            DisplayMode.Fill, "monitor", 1234, HardwareDecodeMode.Disabled, true, true);

        var json = JsonSerializer.Serialize(options);
        var roundTrip = JsonSerializer.Deserialize<RendererStartOptions>(json);

        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.RenderTestPattern);
        Assert.Equal("rtsp://127.0.0.1:8554/self-test", roundTrip.Url);
    }

    [Fact]
    public void EndToEndVerification_IsDistinctFromWindowVisible()
    {
        Assert.NotEqual(RendererEventType.WallpaperVisible, RendererEventType.WallpaperEndToEndVerified);
        Assert.Equal("WallpaperEndToEndVerified", nameof(RendererEventType.WallpaperEndToEndVerified));
    }

    [Fact]
    public void SelfTestPatternMarkers_AreDistinctAndCentralized()
    {
        Assert.Equal(4, RendererSelfTestPattern.MarkerPositions.Count);
        Assert.Equal(4, new[]
        {
            RendererSelfTestPattern.RedMarker,
            RendererSelfTestPattern.GreenMarker,
            RendererSelfTestPattern.BlueMarker,
            RendererSelfTestPattern.YellowMarker
        }.Distinct().Count());
        Assert.All(RendererSelfTestPattern.MarkerPositions, position =>
        {
            Assert.InRange(position.X, 0.25, 0.75);
            Assert.InRange(position.Y, 0.05, 0.95);
        });
    }
}
