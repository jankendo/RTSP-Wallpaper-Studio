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

public sealed record RendererStartOptions(
    string Url,
    string? UserName,
    string? Password,
    TransportMode Transport,
    int NetworkCachingMs,
    DisplayMode DisplayMode,
    string MonitorId,
    int ParentProcessId);

public sealed record RendererStatusMessage(
    PlaybackStatus Status,
    string? Message,
    DateTimeOffset Timestamp,
    int? VideoWidth = null,
    int? VideoHeight = null,
    string? Codec = null);
