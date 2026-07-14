namespace RTSPWallpaperStudio.Renderer;

internal sealed record PlaybackHealthSnapshot(
    bool IsPlaying,
    int VoutCount,
    long MediaTimeMs,
    DateTimeOffset? LastProgressAt,
    TimeSpan ProgressAge)
{
    public string ToDiagnosticString() =>
        $"state={(IsPlaying ? "Playing" : "NotPlaying")}, vout={VoutCount}, mediaTimeMs={MediaTimeMs}, progressAgeMs={ProgressAge.TotalMilliseconds:0}";
}
