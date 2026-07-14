namespace RTSPWallpaperStudio.Core.Domain;

public enum ConnectionTestStage
{
    ValidatingUrl,
    ResolvingHost,
    CheckingTcpPort,
    InitializingLibVlc,
    OpeningRtsp,
    WaitingForPlaying,
    WaitingForVideoOutput,
    ReadingMetadata,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record ConnectionTestRequest(
    string Url,
    string? UserName,
    string? Password,
    TransportMode Transport,
    int NetworkCachingMs,
    int TimeoutSeconds,
    HardwareDecodeMode HardwareDecode,
    bool MuteAudio = true);

public sealed record ConnectionTestProgress(
    ConnectionTestStage Stage,
    int Percent,
    string Message,
    IReadOnlyList<string> Steps,
    string? ActualTransport = null);

public sealed record RtspStreamInformation(
    int VideoTrackCount,
    int? Width,
    int? Height,
    string? Codec,
    string? MediaState,
    int VoutCount);
