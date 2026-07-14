namespace RTSPWallpaperStudio.Core.Services;

public sealed class ReconnectPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)
    ];

    public TimeSpan GetDelay(int attempt, double jitterFactor = 0.2, double randomUnit = 0.5)
    {
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (jitterFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFactor));
        }

        var baseDelay = Delays[Math.Min(attempt, Delays.Length - 1)];
        var multiplier = 1 + ((randomUnit * 2) - 1) * jitterFactor;
        return TimeSpan.FromMilliseconds(Math.Max(1, baseDelay.TotalMilliseconds * multiplier));
    }

    public bool ShouldResetAfterStablePlayback(TimeSpan elapsed) => elapsed >= TimeSpan.FromSeconds(30);
}
