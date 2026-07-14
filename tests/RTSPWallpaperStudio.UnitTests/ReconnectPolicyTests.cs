using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.UnitTests;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void Delay_UsesCappedExponentialBackoff()
    {
        var policy = new ReconnectPolicy();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0, 0, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetDelay(20, 0, 0.5));
    }

    [Fact]
    public void Delay_StaysWithinJitterRange()
    {
        var policy = new ReconnectPolicy();

        Assert.Equal(TimeSpan.FromSeconds(9.6), policy.GetDelay(3, 0.2, 1));
        Assert.Equal(TimeSpan.FromSeconds(6.4), policy.GetDelay(3, 0.2, 0));
    }

    [Fact]
    public void PlaybackStallDetector_OnlyFlagsPlayingVideoWithStaleProgress()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = now - TimeSpan.FromSeconds(9);

        Assert.True(PlaybackStallDetector.IsStalled(true, 1, stale, now, TimeSpan.FromSeconds(8)));
        Assert.False(PlaybackStallDetector.IsStalled(false, 1, stale, now, TimeSpan.FromSeconds(8)));
        Assert.False(PlaybackStallDetector.IsStalled(true, 0, stale, now, TimeSpan.FromSeconds(8)));
        Assert.False(PlaybackStallDetector.IsStalled(true, 1, now - TimeSpan.FromSeconds(2), now, TimeSpan.FromSeconds(8)));
    }
}
