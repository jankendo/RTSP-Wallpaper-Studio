using System.Text.Json.Serialization;

namespace RTSPWallpaperStudio.Core.Domain;

public enum PlaybackStatus
{
    Stopped,
    Starting,
    Connecting,
    Buffering,
    Playing,
    PausedByPolicy,
    Reconnecting,
    Failed,
    Disposed
}

public enum TransportMode
{
    Automatic,
    Tcp,
    Udp
}

public enum DisplayMode
{
    Fill,
    Fit,
    Stretch,
    Center,
    OneToOne
}

public enum WallpaperMode
{
    PerDisplay,
    Duplicate,
    Span
}

public enum HardwareDecodeMode
{
    Automatic,
    Enabled,
    Disabled
}

public enum AudioMode
{
    Muted,
    SelectedOnly,
    All
}

public sealed class RtspProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "SwitchBot 3MP";
    public string Url { get; set; } = "rtsp://127.0.0.1:8554/switchbot3mp";
    public string UserName { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public TransportMode Transport { get; set; } = TransportMode.Tcp;
    public int NetworkCachingMs { get; set; } = 300;
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public bool MuteAudio { get; set; } = true;
    public HardwareDecodeMode HardwareDecode { get; set; } = HardwareDecodeMode.Automatic;
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Fill;
    public WallpaperMode WallpaperMode { get; set; } = WallpaperMode.PerDisplay;
    public PlaybackStatus LastStatus { get; set; } = PlaybackStatus.Stopped;
    public DateTimeOffset? LastConnectedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<RtspProfile> Profiles { get; set; } = [new RtspProfile()];
    public string? SelectedProfileId { get; set; }
    public string? SelectedMonitorId { get; set; }
    public bool RestoreLastWallpaper { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 3;
    public string ThemeMode { get; set; } = "Dark";
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static RectD FromBounds(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public static RectD Union(IEnumerable<RectD> rectangles)
    {
        var values = rectangles.ToArray();
        if (values.Length == 0)
        {
            return new RectD();
        }

        var left = values.Min(x => x.X);
        var top = values.Min(x => x.Y);
        var right = values.Max(x => x.Right);
        var bottom = values.Max(x => x.Bottom);
        return FromBounds(left, top, right, bottom);
    }
}

public sealed class MonitorInfo
{
    public required string PersistentId { get; init; }
    public required string SourceDeviceName { get; init; }
    public required string FriendlyName { get; init; }
    public required RectD Bounds { get; init; }
    public required RectD WorkArea { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool IsPrimary { get; init; }
    public int ScalePercentage { get; init; } = 100;
    public int RotationDegrees { get; init; }

    [JsonIgnore]
    public string DisplaySummary => $"{FriendlyName} · {Width}×{Height} · {ScalePercentage}%";
}

public sealed class MonitorAssignment
{
    public required string MonitorId { get; init; }
    public required string ProfileId { get; init; }
}

public sealed record IpcEnvelope(string Command, string? Payload = null, string? RequestId = null);

public enum IpcMessageKind
{
    Command,
    Event,
    Heartbeat
}

public sealed record IpcMessage(
    IpcMessageKind Kind,
    string Name,
    string? Payload = null,
    string? RequestId = null,
    string? RendererId = null,
    int SchemaVersion = 1);

public sealed record RendererStartOptions(
    string Url,
    string? UserName,
    string? Password,
    TransportMode Transport,
    int NetworkCachingMs,
    DisplayMode DisplayMode,
    string MonitorId,
    int ParentProcessId,
    HardwareDecodeMode HardwareDecode = HardwareDecodeMode.Automatic,
    bool MuteAudio = true);

public sealed record RendererStatusMessage(
    PlaybackStatus Status,
    string? Message,
    DateTimeOffset Timestamp,
    int? VideoWidth = null,
    int? VideoHeight = null,
    string? Codec = null);

public enum DesktopLayoutStrategy
{
    Unknown,
    LegacyWorkerW,
    RaisedDesktop
}

public enum RendererEventType
{
    RendererReady,
    LibVlcInitialized,
    StreamOpening,
    MediaParsed,
    VideoTrackDetected,
    VideoOutputReady,
    DesktopHostDiscovered,
    AttachmentSucceeded,
    AttachmentFailed,
    WallpaperVisible,
    PlaybackRunning,
    Buffering,
    Reconnecting,
    PlaybackError,
    ExplorerRestartDetected,
    Reattached,
    Stopped,
    FatalError,
    Heartbeat,
    PlaybackStalled
}

public static class RendererErrorCodes
{
    public const string VlcInitFailed = "VLC_INIT_FAILED";
    public const string VlcNativeMissing = "VLC_NATIVE_MISSING";
    public const string VlcPluginPathInvalid = "VLC_PLUGIN_PATH_INVALID";
    public const string VlcArchitectureMismatch = "VLC_ARCHITECTURE_MISMATCH";
    public const string RtspOpenFailed = "RTSP_OPEN_FAILED";
    public const string RtspPortClosed = "RTSP_PORT_CLOSED";
    public const string RtspUnauthorized = "RTSP_UNAUTHORIZED";
    public const string RtspNoVideoTrack = "RTSP_NO_VIDEO_TRACK";
    public const string RtspFirstFrameTimeout = "RTSP_FIRST_FRAME_TIMEOUT";
    public const string RtspPlaybackStalled = "RTSP_PLAYBACK_STALLED";
    public const string DesktopProgmanNotFound = "DESKTOP_PROGMAN_NOT_FOUND";
    public const string DesktopShellViewNotFound = "DESKTOP_SHELL_VIEW_NOT_FOUND";
    public const string DesktopHostNotFound = "DESKTOP_HOST_NOT_FOUND";
    public const string DesktopUnsupportedLayout = "DESKTOP_UNSUPPORTED_LAYOUT";
    public const string WallpaperSetParentFailed = "WALLPAPER_SET_PARENT_FAILED";
    public const string WallpaperParentMismatch = "WALLPAPER_PARENT_MISMATCH";
    public const string WallpaperStyleUpdateFailed = "WALLPAPER_STYLE_UPDATE_FAILED";
    public const string WallpaperCoordinateMappingFailed = "WALLPAPER_COORDINATE_MAPPING_FAILED";
    public const string WallpaperRectValidationFailed = "WALLPAPER_RECT_VALIDATION_FAILED";
    public const string WallpaperZOrderValidationFailed = "WALLPAPER_ZORDER_VALIDATION_FAILED";
    public const string ExplorerReattachFailed = "EXPLORER_REATTACH_FAILED";
    public const string RendererCrashLoop = "RENDERER_CRASH_LOOP";
}

public sealed record RendererMetrics(
    int? ProcessId,
    nint RendererHwnd,
    nint ParentHwnd,
    nint ExpectedParentHwnd,
    int VoutCount,
    string MediaState,
    string? Codec,
    int? VideoWidth,
    int? VideoHeight,
    int ReconnectCount,
    DesktopLayoutStrategy DesktopStrategy,
    RectD RendererRect,
    RectD MonitorRect,
    long MediaTimeMs = -1,
    DateTimeOffset? LastVideoProgressAt = null,
    double? VideoProgressAgeSeconds = null);

public sealed record RendererEvent(
    string RendererId,
    RendererEventType Type,
    DateTimeOffset Timestamp,
    string? ErrorCode = null,
    string? UserMessage = null,
    string? TechnicalDetails = null,
    RendererMetrics? Metrics = null);

public sealed class RuntimeState
{
    public int SchemaVersion { get; set; } = 1;
    public bool PreviousShutdownClean { get; set; } = true;
    public bool WallpaperApplyInProgress { get; set; }
    public string? LastSuccessfulProfileId { get; set; }
    public string? LastSuccessfulMonitorId { get; set; }
    public int? LastRendererPid { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
}
