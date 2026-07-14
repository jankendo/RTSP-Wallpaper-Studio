using System.Windows;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Renderer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = CommandLine.Parse(e.Args);
        if (string.IsNullOrWhiteSpace(args.PipeName))
        {
            Shutdown(2);
            return;
        }

        try
        {
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception)
        {
            // The window remains available for diagnostics even if native VLC initialization fails.
        }

        var window = new RendererWindow(args.PipeName, args.MonitorId, args.ParentProcessId);
        MainWindow = window;
        window.Show();
    }

    private sealed record CommandLine(string PipeName, string MonitorId, int ParentProcessId)
    {
        public static CommandLine Parse(string[] args)
        {
            string value(string key) =>
                args.SkipWhile(x => !x.Equals(key, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? string.Empty;
            var parent = int.TryParse(value("--parent-pid"), out var pid) ? pid : -1;
            return new CommandLine(value("--pipe"), value("--monitor-id"), parent);
        }
    }
}
