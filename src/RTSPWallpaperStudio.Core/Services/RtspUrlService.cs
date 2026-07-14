using System.Net;
using System.Text.RegularExpressions;

namespace RTSPWallpaperStudio.Core.Services;

public sealed record RtspUrlParts(string Url, string? UserName, string? Password);

public static partial class RtspUrlService
{
    private static readonly string[] AcceptedSchemes = ["rtsp", "rtsps"];

    public static bool TryNormalize(string? input, out RtspUrlParts parts, out string error)
    {
        parts = new RtspUrlParts(string.Empty, null, null);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            error = "RTSP URLを入力してください。";
            return false;
        }

        if (!AcceptedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = "rtsp:// または rtsps:// のURLだけを指定できます。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535)
        {
            error = "RTSPサーバーのホスト名またはポートが正しくありません。";
            return false;
        }

        var userName = (string?)null;
        var password = (string?)null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var separator = uri.UserInfo.IndexOf(':');
            var rawUser = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            var rawPassword = separator < 0 ? null : uri.UserInfo[(separator + 1)..];
            userName = Uri.UnescapeDataString(rawUser);
            password = rawPassword is null ? null : Uri.UnescapeDataString(rawPassword);
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };
        parts = new RtspUrlParts(builder.Uri.ToString(), userName, password);
        return true;
    }

    public static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return "<empty-or-invalid-rtsp-url>";
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]),
            Password = "***",
            Query = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?<redacted>"
        };
        return builder.Uri.ToString();
    }

    public static bool IsLocalHost(Uri uri) =>
        IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address) ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^a-zA-Z0-9._-]", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNameRegex();

    public static string ToSafeFileName(string value) => SafeNameRegex().Replace(value, "_");
}
