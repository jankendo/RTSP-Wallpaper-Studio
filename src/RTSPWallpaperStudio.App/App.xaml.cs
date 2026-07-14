using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.App.ViewModels;
using RTSPWallpaperStudio.Infrastructure.Diagnostics;
using RTSPWallpaperStudio.Infrastructure.Ipc;
using RTSPWallpaperStudio.Infrastructure.Paths;
using RTSPWallpaperStudio.Infrastructure.Security;
using RTSPWallpaperStudio.Infrastructure.Settings;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, $"RTSPWallpaperStudio-{Environment.UserName}", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("RTSP Wallpaper Studioはすでに起動しています。", "起動できません", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureLogging(logging => logging.AddConsole())
            .ConfigureServices(services =>
            {
                services.AddSingleton<AppPathService>();
                services.AddSingleton<JsonSettingsStore>();
                services.AddSingleton<ProtectedSecretStore>();
                services.AddSingleton<ConnectionTester>();
                services.AddSingleton<DesktopMonitorProvider>();
                services.AddSingleton<RendererProcessManager>();
                services.AddSingleton<MainViewModel>();
            })
            .Build();

        await _host.StartAsync();
        var window = new MainWindow(_host.Services.GetRequiredService<MainViewModel>());
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
