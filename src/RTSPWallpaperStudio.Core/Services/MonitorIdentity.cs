namespace RTSPWallpaperStudio.Core.Services;

public static class MonitorIdentity
{
    public static string Create(string sourceDeviceName, string? devicePath, int width, int height) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sourceDeviceName}|{devicePath}|{width}x{height}")))[..16];

    public static string? Match(IEnumerable<(string Id, string SourceDeviceName)> monitors, string? previousId, string? previousSourceName)
    {
        if (!string.IsNullOrWhiteSpace(previousId) && monitors.Any(x => x.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase)))
        {
            return previousId;
        }

        var match = monitors.FirstOrDefault(x => x.SourceDeviceName.Equals(previousSourceName, StringComparison.OrdinalIgnoreCase));
        return match == default ? null : match.Id;
    }
}
