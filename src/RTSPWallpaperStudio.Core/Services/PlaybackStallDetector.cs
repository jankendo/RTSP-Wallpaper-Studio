namespace RTSPWallpaperStudio.Core.Services;

public static class PlaybackStallDetector
{
    public static bool IsStalled(
        bool isPlaying,
        int voutCount,
        DateTimeOffset? lastProgressAt,
        DateTimeOffset now,
        TimeSpan threshold)
    {
        if (!isPlaying || voutCount <= 0 || lastProgressAt is null || threshold <= TimeSpan.Zero)
        {
            return false;
        }

        return now - lastProgressAt.Value >= threshold;
    }
}
