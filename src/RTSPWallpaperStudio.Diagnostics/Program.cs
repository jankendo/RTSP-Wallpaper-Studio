using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Infrastructure.Diagnostics;
using RTSPWallpaperStudio.Infrastructure.Ipc;
using RTSPWallpaperStudio.Infrastructure.Relay;
using RTSPWallpaperStudio.Infrastructure.Startup;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.Diagnostics;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var options = Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine("Usage: RTSPWallpaperStudio.Diagnostics.exe --url <rtsp-url> [--transport tcp|udp|automatic] [--timeout 10] [--cache 300] [--start-go2rtc] [--wallpaper] [--ipc-smoke]");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --startup-status");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --startup-enable [--startup-exe <RTSPWallpaperStudio.App.exe>]");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --startup-disable");
            return 0;
        }

        if (options.StartupAction is not null)
        {
            return RunStartupCommand(options);
        }

        if (options.Wallpaper || options.IpcSmoke)
        {
            Environment.SetEnvironmentVariable("RTSP_WALLPAPER_IPC_TRACE", "1");
        }

        Go2RtcProcessManager? relay = null;
        if (options.StartGo2Rtc)
        {
            using var relayLoggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            relay = new Go2RtcProcessManager(relayLoggerFactory.CreateLogger<Go2RtcProcessManager>());
            var relayResult = await relay.EnsureStartedAsync();
            Console.WriteLine(JsonSerializer.Serialize(relayResult, JsonOptions));
            if (!relayResult.Available)
            {
                return 10;
            }
        }

        if (options.IpcSmoke)
        {
            return await RunIpcSmokeAsync(options, relay);
        }

        var request = new ConnectionTestRequest(options.Url, options.UserName, options.Password,
            options.Transport, options.CacheMs, options.TimeoutSeconds, options.HardwareDecode, true);
        var tester = new ConnectionTester();
        var progress = new Progress<ConnectionTestProgress>(value =>
            Console.Error.WriteLine($"[{value.Percent,3}%] {value.Stage}: {value.Message} transport={value.ActualTransport}"));
        var result = await tester.TestAsync(request, progress);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        if (!result.Success || !options.Wallpaper)
        {
            if (relay is not null) await relay.StopOwnedAsync();
            return result.Success ? 0 : 2;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        await using var rendererManager = new RendererProcessManager(loggerFactory.CreateLogger<RendererProcessManager>());
        try
        {
            var monitor = new DesktopMonitorProvider().GetMonitors().FirstOrDefault();
            if (monitor is null)
            {
                Console.Error.WriteLine("No monitor was found.");
                return 11;
            }

            var wallpaperResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            rendererManager.RendererEventReceived += (_, rendererEvent) =>
            {
                Console.Error.WriteLine($"[renderer] {rendererEvent.Type} code={rendererEvent.ErrorCode ?? "-"} message={rendererEvent.UserMessage ?? "-"}");
                if (rendererEvent.Type == RendererEventType.WallpaperVisible)
                {
                    wallpaperResult.TrySetResult(true);
                }
                else if (rendererEvent.Type is RendererEventType.FatalError or RendererEventType.PlaybackError or RendererEventType.AttachmentFailed)
                {
                    wallpaperResult.TrySetResult(false);
                }
            };

            var startOptions = new RendererStartOptions(options.Url, options.UserName, options.Password,
                options.Transport, options.CacheMs, DisplayMode.Fill, monitor.PersistentId, Environment.ProcessId,
                options.HardwareDecode, true);
            await rendererManager.StartAsync(startOptions).WaitAsync(TimeSpan.FromSeconds(15));
            var wallpaperSucceeded = await wallpaperResult.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(30, options.TimeoutSeconds + 20)));
            Console.WriteLine($"WALLPAPER_RESULT={(wallpaperSucceeded ? "SUCCESS" : "FAILED")}");
            return wallpaperSucceeded ? 0 : 3;
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine($"WALLPAPER_RESULT=FAILED code=RTSP_RENDERER_TIMEOUT message={ex.Message}");
            return 12;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WALLPAPER_RESULT=FAILED code=RTSP_RENDERER_START_FAILED message={ex.Message}");
            return 13;
        }
        finally
        {
            await rendererManager.StopAllAsync();
            if (relay is not null) await relay.StopOwnedAsync();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static DiagnosticOptions Parse(string[] args)
    {
        var url = "rtsp://127.0.0.1:8554/switchbot3mp";
        var transport = TransportMode.Tcp;
        var timeout = 10;
        var cache = 300;
        var hardware = HardwareDecodeMode.Automatic;
        string? user = null;
        string? password = null;
        var startGo2Rtc = false;
        var wallpaper = false;
        var ipcSmoke = false;
        string? startupAction = null;
        string? startupExecutable = null;
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--help": case "-h": help = true; break;
                case "--url": url = args[++i]; break;
                case "--user": user = args[++i]; break;
                case "--password": password = args[++i]; break;
                case "--timeout": timeout = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cache": cache = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--start-go2rtc": startGo2Rtc = true; break;
                case "--wallpaper": wallpaper = true; break;
                case "--ipc-smoke": ipcSmoke = true; break;
                case "--startup-status": startupAction = "status"; break;
                case "--startup-enable": startupAction = "enable"; break;
                case "--startup-disable": startupAction = "disable"; break;
                case "--startup-exe": startupExecutable = args[++i]; break;
                case "--transport": transport = Enum.Parse<TransportMode>(args[++i], ignoreCase: true); break;
                case "--hardware": hardware = Enum.Parse<HardwareDecodeMode>(args[++i], ignoreCase: true); break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new DiagnosticOptions(url, user, password, transport, timeout, cache, hardware, startGo2Rtc, wallpaper, ipcSmoke,
            startupAction, startupExecutable, help);
    }

    private static int RunStartupCommand(DiagnosticOptions options)
    {
        var service = new StartupRegistrationService();
        if (options.StartupAction == "status")
        {
            var status = service.GetStatus();
            Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
            return status.Success ? 0 : 20;
        }

        var result = service.SetEnabled(options.StartupAction == "enable", options.StartupExecutable);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.Success ? 0 : 21;
    }

    private static async Task<int> RunIpcSmokeAsync(DiagnosticOptions options, Go2RtcProcessManager? relay)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        await using var rendererManager = new RendererProcessManager(loggerFactory.CreateLogger<RendererProcessManager>());
        try
        {
            var monitor = new DesktopMonitorProvider().GetMonitors().FirstOrDefault();
            if (monitor is null)
            {
                Console.Error.WriteLine("IPC_SMOKE=FAILED code=NO_MONITOR");
                return 11;
            }

            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            rendererManager.RendererEventReceived += (_, rendererEvent) =>
            {
                Console.Error.WriteLine($"[renderer] {rendererEvent.Type} code={rendererEvent.ErrorCode ?? "-"}");
                if (rendererEvent.Type == RendererEventType.RendererReady)
                {
                    result.TrySetResult("READY");
                }
                else if (rendererEvent.Type is RendererEventType.FatalError or RendererEventType.PlaybackError)
                {
                    result.TrySetResult("ERROR");
                }
            };

            await rendererManager.StartAsync(new RendererStartOptions(options.Url, options.UserName, options.Password,
                options.Transport, options.CacheMs, DisplayMode.Fill, monitor.PersistentId, Environment.ProcessId,
                options.HardwareDecode, true)).WaitAsync(TimeSpan.FromSeconds(15));
            var eventResult = await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine($"IPC_SMOKE={eventResult}");
            return eventResult == "READY" ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IPC_SMOKE=FAILED code={ex.GetType().Name} message={ex.Message}");
            return 12;
        }
        finally
        {
            await rendererManager.StopAllAsync();
            if (relay is not null) await relay.StopOwnedAsync();
        }
    }

    private sealed record DiagnosticOptions(string Url, string? UserName, string? Password, TransportMode Transport,
        int TimeoutSeconds, int CacheMs, HardwareDecodeMode HardwareDecode, bool StartGo2Rtc, bool Wallpaper, bool IpcSmoke,
        string? StartupAction, string? StartupExecutable, bool ShowHelp);
}
