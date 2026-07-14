namespace RTSPWallpaperStudio.Infrastructure.Paths;

public sealed class AppPathService
{
    public AppPathService()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RTSPWallpaperStudio");
        Logs = Path.Combine(Root, "Logs");
        CrashReports = Path.Combine(Root, "CrashReports");
        Screenshots = Path.Combine(Root, "Screenshots");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(CrashReports);
        Directory.CreateDirectory(Screenshots);
    }

    public string Root { get; }
    public string Logs { get; }
    public string CrashReports { get; }
    public string Screenshots { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string BackupFile => Path.Combine(Root, "settings.json.bak");
}
