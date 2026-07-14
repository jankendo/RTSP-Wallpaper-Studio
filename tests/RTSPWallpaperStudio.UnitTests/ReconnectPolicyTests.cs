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

    [Fact]
    public void PlaybackProgressTracker_DoesNotPrimeDuringFastForwardCatchUp()
    {
        var tracker = new PlaybackProgressTracker();
        var start = DateTimeOffset.UtcNow;

        tracker.Observe(0, start);
        tracker.Observe(3000, start.AddSeconds(1));

        var duringCatchUp = tracker.Snapshot(start.AddSeconds(1));
        Assert.False(duringCatchUp.IsPrimed);
        Assert.Null(duringCatchUp.LastProgressAt);

        for (var index = 1; index <= 12; index++)
        {
            tracker.Observe(3000 + index * 66, start.AddSeconds(1).AddMilliseconds(index * 66));
        }

        var stable = tracker.Snapshot(start.AddSeconds(2));
        Assert.True(stable.IsPrimed);
        Assert.NotNull(stable.LastProgressAt);
    }

    [Fact]
    public void PlaybackProgressTracker_ResetsStableGateWhenMediaTimeStopsOrRewinds()
    {
        var tracker = new PlaybackProgressTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(0, start);

        for (var index = 1; index <= 12; index++)
        {
            tracker.Observe(index * 100, start.AddMilliseconds(index * 100));
        }

        Assert.True(tracker.Snapshot(start.AddSeconds(2)).IsPrimed);

        tracker.Observe(1200, start.AddSeconds(2.1));
        tracker.Observe(900, start.AddSeconds(2.2));

        var rewound = tracker.Snapshot(start.AddSeconds(2.2));
        Assert.False(rewound.IsPrimed);
    }

    [Fact]
    public void PlaybackProgressTracker_IgnoresDuplicateWatchdogSamples()
    {
        var tracker = new PlaybackProgressTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(0, start);

        for (var index = 1; index <= 8; index++)
        {
            var timestamp = start.AddMilliseconds(index * 150);
            tracker.Observe(index * 150, timestamp);
            tracker.Observe(index * 150, timestamp.AddMilliseconds(10));
        }

        Assert.True(tracker.Snapshot(start.AddSeconds(2)).IsPrimed);
    }
}
