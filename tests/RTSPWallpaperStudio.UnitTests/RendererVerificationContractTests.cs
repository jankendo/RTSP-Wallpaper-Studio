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

    [Fact]
    public void ShellComposition_RequiresEveryDesktopSafetyGate()
    {
        var verified = new RendererShellCompositionMetrics(
            RendererFramesVisibleOnDesktop: true,
            RendererVisibleAboveStaticWallpaper: true,
            RendererFrameDetectedOnComposedDesktop: true,
            RendererAnimationDetectedOnComposedDesktop: true,
            RendererVisibleInBackgroundOnlyRegion: true,
            DesktopIconHostLocated: true,
            DesktopIconHostVisible: true,
            DesktopIconsAboveRenderer: true,
            TaskbarLocated: true,
            TaskbarVisible: true,
            TaskbarAboveRenderer: true,
            RendererNotInAltTab: true,
            RendererNotInTaskbar: true,
            RendererDoesNotOwnForeground: true,
            DesktopInputAvailable: true);

        Assert.True(RendererShellCompositionContract.IsVerified(verified));
        Assert.False(RendererShellCompositionContract.IsVerified(verified with { RendererVisibleAboveStaticWallpaper = false }));
        Assert.False(RendererShellCompositionContract.IsVerified(verified with { RendererAnimationDetectedOnComposedDesktop = false }));
        Assert.False(RendererShellCompositionContract.IsVerified(verified with { RendererVisibleInBackgroundOnlyRegion = false }));
        Assert.False(RendererShellCompositionContract.IsVerified(verified with { DesktopIconsAboveRenderer = false }));
        Assert.False(RendererShellCompositionContract.IsVerified(verified with { TaskbarAboveRenderer = false }));
        Assert.False(RendererShellCompositionContract.IsVerified(verified with { DesktopInputAvailable = false }));
    }
}
