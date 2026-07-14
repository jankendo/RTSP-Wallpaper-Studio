using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.App.ViewModels;
using RTSPWallpaperStudio.Infrastructure.Diagnostics;
using RTSPWallpaperStudio.Infrastructure.Ipc;
using RTSPWallpaperStudio.Infrastructure.Logging;
using RTSPWallpaperStudio.Infrastructure.Paths;
using RTSPWallpaperStudio.Infrastructure.Relay;
using RTSPWallpaperStudio.Infrastructure.Security;
using RTSPWallpaperStudio.Infrastructure.Settings;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private IHost? _host;
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, $"RTSPWallpaperStudio-{Environment.UserName}", out var isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("RTSP Wallpaper Studioはすでに起動しています。", "起動できません", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        var startupOptions = new AppStartupOptions(
            e.Args.Any(x => x.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase)),
            e.Args.Any(x => x.Equals("--stop-all", StringComparison.OrdinalIgnoreCase)),
            e.Args.Any(x => x.Equals("--reset-desktop", StringComparison.OrdinalIgnoreCase)));
        var paths = new AppPathService();
        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddProvider(new FileLoggerProvider(paths.Logs));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(paths);
                services.AddSingleton(startupOptions);
                services.AddSingleton<JsonSettingsStore>();
                services.AddSingleton<ProtectedSecretStore>();
                services.AddSingleton<ConnectionTester>();
                services.AddSingleton<DesktopMonitorProvider>();
                services.AddSingleton<RuntimeStateStore>();
                services.AddSingleton<RendererProcessManager>();
                services.AddSingleton<Go2RtcProcessManager>();
                services.AddSingleton<MainViewModel>();
            })
            .Build();

        await _host.StartAsync();
        await _host.Services.GetRequiredService<Go2RtcProcessManager>().EnsureStartedAsync();
        _viewModel = _host.Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                var host = _host;
                var viewModel = _viewModel;
                Task.Run(async () =>
                {
                    await host.Services.GetRequiredService<RendererProcessManager>().StopAllAsync().ConfigureAwait(false);
                    await host.Services.GetRequiredService<Go2RtcProcessManager>().StopOwnedAsync().ConfigureAwait(false);
                    if (viewModel is not null)
                    {
                        await viewModel.MarkCleanShutdownAsync().ConfigureAwait(false);
                    }

                    await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"終了処理に失敗しました: {ex}");
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
