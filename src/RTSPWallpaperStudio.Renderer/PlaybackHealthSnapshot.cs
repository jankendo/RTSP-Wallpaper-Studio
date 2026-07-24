namespace RTSPWallpaperStudio.Renderer;

internal sealed record PlaybackHealthSnapshot(
    bool IsPlaying,
    int VoutCount,
    long MediaTimeMs,
    DateTimeOffset? LastProgressAt,
    TimeSpan ProgressAge,
    bool IsPrimed,
    int StableSamples,
    double? Rate)
{
    public string ToDiagnosticString() =>
        $"state={(IsPlaying ? "Playing" : "NotPlaying")}, vout={VoutCount}, mediaTimeMs={MediaTimeMs}, progressAgeMs={ProgressAge.TotalMilliseconds:0}, primed={IsPrimed}, stableSamples={StableSamples}, rate={Rate:0.00}x";
}
