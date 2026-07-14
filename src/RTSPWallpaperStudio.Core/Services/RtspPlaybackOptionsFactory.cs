using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Core.Services;

public sealed record RtspPlaybackOptions(
    string Url,
    string? UserName,
    string? Password,
    TransportMode Transport,
    int NetworkCachingMs,
    HardwareDecodeMode HardwareDecode,
    bool MuteAudio = true);

public static class RtspLocationBuilder
{
    public static string Build(string url, string? userName, string? password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("RTSP URLが正しくありません。", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(userName))
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            UserName = userName,
            Password = password ?? string.Empty
        };
        return builder.Uri.ToString();
    }
}

public static class RtspPlaybackOptionsFactory
{
    public static RtspPlaybackOptions Create(ConnectionTestRequest request) =>
        new(request.Url, request.UserName, request.Password, request.Transport,
            request.NetworkCachingMs, request.HardwareDecode, request.MuteAudio);

    public static IReadOnlyList<string> CreateMediaOptions(RtspPlaybackOptions options)
    {
        var values = new List<string>
        {
            $":network-caching={Math.Clamp(options.NetworkCachingMs, 50, 5000)}"
        };

        if (options.Transport == TransportMode.Tcp)
        {
            values.Add(":rtsp-tcp");
        }

        if (options.HardwareDecode == HardwareDecodeMode.Disabled)
        {
            values.Add(":avcodec-hw=none");
        }
        else if (options.HardwareDecode == HardwareDecodeMode.Enabled)
        {
            values.Add(":avcodec-hw=dxva2");
        }

        if (options.MuteAudio)
        {
            values.Add(":no-audio");
        }

        return values;
    }

    public static string DisplayTransport(TransportMode requested, string? actualTransport = null) =>
        string.IsNullOrWhiteSpace(actualTransport)
            ? requested switch
            {
                TransportMode.Tcp => "TCP",
                TransportMode.Udp => "UDP",
                _ => "自動"
            }
            : actualTransport;
}
