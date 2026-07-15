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
            Console.WriteLine("Usage: RTSPWallpaperStudio.Diagnostics.exe --url <rtsp-url> [--transport tcp|udp|automatic] [--timeout 10] [--cache 300] [--start-go2rtc] [--wallpaper|--wallpaper-only] [--render-test-pattern] [--desktop-probe] [--hold-seconds 30] [--ipc-smoke]");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --probe-hwnd <hex-or-decimal-hwnd> [--probe-seconds 1]");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --startup-status");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --startup-enable [--startup-exe <RTSPWallpaperStudio.App.exe>]");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --startup-disable");
            Console.WriteLine("       RTSPWallpaperStudio.Diagnostics.exe --create-diagnostics-package");
            return 0;
        }

        if (options.StartupAction is not null)
        {
            return RunStartupCommand(options);
        }

        if (options.CreateDiagnosticsPackage)
        {
            var packagePath = await new DiagnosticsPackageService(new RTSPWallpaperStudio.Infrastructure.Paths.AppPathService())
                .CreateAsync(null, "CLI diagnostics package command");
            Console.WriteLine($"DIAGNOSTICS_PACKAGE={packagePath}");
            return 0;
        }

        if (options.ProbeHwnd is not null)
        {
            return await RunDesktopProbeAsync(options);
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

        if (options.WallpaperOnly || (options.RenderTestPattern && options.Wallpaper))
        {
            return await RunWallpaperOnlyAsync(options, relay);
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
            RendererMetrics? visibleMetrics = null;
            rendererManager.RendererEventReceived += (_, rendererEvent) =>
            {
                var metrics = rendererEvent.Metrics is { } m
                    ? $" metrics=state:{m.MediaState},vout:{m.VoutCount},time:{m.MediaTimeMs},age:{m.VideoProgressAgeSeconds:0.0}s,reconnect:{m.ReconnectCount},visible:{m.WindowVisible},class:{m.WindowClass},hwnd:0x{m.RendererHwnd.ToInt64():X},parent:0x{m.ParentHwnd.ToInt64():X},expectedParent:0x{m.ExpectedParentHwnd.ToInt64():X},rect:{m.RendererRect},monitor:{m.MonitorRect},decoded:{m.Presentation?.DecodedFrameCount},paint:{m.Presentation?.PaintCount},presented:{m.Presentation?.PresentedFrameCount},checksum:0x{m.Presentation?.LastPresentedChecksum:X}"
                    : string.Empty;
                Console.Error.WriteLine($"[renderer] {rendererEvent.Type} code={rendererEvent.ErrorCode ?? "-"} message={rendererEvent.UserMessage ?? "-"}{metrics} details={rendererEvent.TechnicalDetails ?? "-"}");
                if (rendererEvent.Type == RendererEventType.WallpaperEndToEndVerified)
                {
                    visibleMetrics = rendererEvent.Metrics;
                    wallpaperResult.TrySetResult(true);
                }
                else if (rendererEvent.Type is RendererEventType.FatalError or RendererEventType.PlaybackError or RendererEventType.AttachmentFailed)
                {
                    wallpaperResult.TrySetResult(false);
                }
            };

            var startOptions = new RendererStartOptions(options.Url, options.UserName, options.Password,
                options.Transport, options.CacheMs, DisplayMode.Fill, monitor.PersistentId, Environment.ProcessId,
                options.HardwareDecode, true, options.RenderTestPattern);
            await rendererManager.StartAsync(startOptions).WaitAsync(TimeSpan.FromSeconds(15));
            var wallpaperSucceeded = await wallpaperResult.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(30, options.TimeoutSeconds + 20)));
            Console.WriteLine($"WALLPAPER_RESULT={(wallpaperSucceeded ? "SUCCESS" : "FAILED")}");
            if (wallpaperSucceeded && options.DesktopProbe && visibleMetrics is { } metrics)
            {
                var first = DesktopPixelProbe.Sample(metrics.RendererRect);
                await Task.Delay(TimeSpan.FromSeconds(1));
                var second = DesktopPixelProbe.Sample(metrics.RendererRect);
                var diff = DesktopPixelProbe.Compare(first, second);
                Console.WriteLine($"DESKTOP_PROBE={JsonSerializer.Serialize(new { rendererHwnd = metrics.RendererHwnd.ToInt64(), parentHwnd = metrics.ParentHwnd.ToInt64(), expectedParentHwnd = metrics.ExpectedParentHwnd.ToInt64(), metrics.RendererRect, metrics.MonitorRect, metrics.WindowVisible, metrics.WindowClass, firstSampleCount = first.SampleCount, firstNonBlackSampleCount = first.NonBlackSampleCount, firstAverageLuma = first.AverageLuma, firstDiagnostic = first.Diagnostic, secondSampleCount = second.SampleCount, secondNonBlackSampleCount = second.NonBlackSampleCount, secondAverageLuma = second.AverageLuma, secondDiagnostic = second.Diagnostic, diff.ComparedSamples, diff.ChangedSamples, diff.AverageAbsoluteRgbDelta, diff.HasMovement }, JsonOptions)}");
            }
            if (wallpaperSucceeded && options.HoldSeconds > 0)
            {
                Console.WriteLine($"WALLPAPER_HOLD_SECONDS={options.HoldSeconds}");
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
            }

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
        var renderTestPattern = false;
        var wallpaper = false;
        var wallpaperOnly = false;
        var ipcSmoke = false;
        var desktopProbe = false;
        nint? probeHwnd = null;
        var probeSeconds = 1;
        var holdSeconds = 0;
        string? startupAction = null;
        string? startupExecutable = null;
        var help = false;
        var createDiagnosticsPackage = false;
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
                case "--wallpaper-only": wallpaper = true; wallpaperOnly = true; break;
                case "--render-test-pattern": renderTestPattern = true; wallpaper = true; wallpaperOnly = true; break;
                case "--ipc-smoke": ipcSmoke = true; break;
                case "--desktop-probe": desktopProbe = true; break;
                case "--probe-hwnd":
                    var hwndText = args[++i];
                    probeHwnd = (nint)(hwndText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToInt64(hwndText[2..], 16)
                        : long.Parse(hwndText, CultureInfo.InvariantCulture));
                    break;
                case "--probe-seconds": probeSeconds = Math.Clamp(int.Parse(args[++i], CultureInfo.InvariantCulture), 1, 60); break;
                case "--hold-seconds": holdSeconds = Math.Clamp(int.Parse(args[++i], CultureInfo.InvariantCulture), 0, 3600); break;
                case "--startup-status": startupAction = "status"; break;
                case "--startup-enable": startupAction = "enable"; break;
                case "--startup-disable": startupAction = "disable"; break;
                case "--startup-exe": startupExecutable = args[++i]; break;
                case "--create-diagnostics-package": createDiagnosticsPackage = true; break;
                case "--transport": transport = Enum.Parse<TransportMode>(args[++i], ignoreCase: true); break;
                case "--hardware": hardware = Enum.Parse<HardwareDecodeMode>(args[++i], ignoreCase: true); break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new DiagnosticOptions(url, user, password, transport, timeout, cache, hardware, startGo2Rtc, renderTestPattern, wallpaper, wallpaperOnly, ipcSmoke,
            desktopProbe, probeHwnd, probeSeconds, holdSeconds, startupAction, startupExecutable, createDiagnosticsPackage, help);
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

    private static async Task<int> RunDesktopProbeAsync(DiagnosticOptions options)
    {
        var hwnd = options.ProbeHwnd!.Value;
        if (!NativeWindowDiagnostics.IsWindow(hwnd) || !DesktopWindowDiagnostics.TryGetScreenRect(hwnd, out var rect))
        {
            Console.WriteLine($"DESKTOP_PROBE=FAILED code=INVALID_HWND hwnd=0x{hwnd.ToInt64():X}");
            return 30;
        }

        var monitors = new DesktopMonitorProvider().GetMonitors();
        var monitor = monitors.FirstOrDefault(x => rect.X >= x.Bounds.X && rect.Y >= x.Bounds.Y &&
                                                   rect.Right <= x.Bounds.Right && rect.Bottom <= x.Bounds.Bottom);
        var discovery = new DesktopHostDiscovery().DiscoverExisting();
        var attachDiagnostic = string.Empty;
        var attachValid = discovery.Success && DesktopAttachmentValidator.Validate(hwnd, discovery, out attachDiagnostic);
        if (!discovery.Success)
        {
            attachDiagnostic = discovery.Diagnostic;
        }

        var rectDiagnostic = string.Empty;
        var rectValid = monitor is not null && DesktopAttachmentValidator.ValidateRect(hwnd, monitor.Bounds, out rectDiagnostic);
        var first = DesktopPixelProbe.Sample(rect);
        await Task.Delay(TimeSpan.FromSeconds(options.ProbeSeconds));
        var second = DesktopPixelProbe.Sample(rect);
        var diff = DesktopPixelProbe.Compare(first, second);
        var result = new
        {
            hwnd = hwnd.ToInt64(),
            processId = NativeWindowDiagnostics.GetProcessId(hwnd),
            className = NativeWindowDiagnostics.GetClassName(hwnd),
            parentHwnd = DesktopWindowDiagnostics.GetParent(hwnd).ToInt64(),
            ownerHwnd = NativeWindowDiagnostics.GetOwner(hwnd).ToInt64(),
            rootHwnd = NativeWindowDiagnostics.GetRoot(hwnd).ToInt64(),
            visible = NativeWindowDiagnostics.IsVisible(hwnd),
            style = $"0x{NativeWindowDiagnostics.GetStyle(hwnd):X}",
            extendedStyle = $"0x{NativeWindowDiagnostics.GetExtendedStyle(hwnd):X}",
            rect,
            monitor = monitor?.Bounds,
            strategy = discovery.Strategy.ToString(),
            expectedParentHwnd = (discovery.Strategy == DesktopLayoutStrategy.RaisedDesktop ? nint.Zero : discovery.HostHwnd).ToInt64(),
            attachValid,
            attachDiagnostic,
            rectValid,
            rectDiagnostic,
            firstSampleCount = first.SampleCount,
            firstNonBlackSampleCount = first.NonBlackSampleCount,
            firstAverageLuma = first.AverageLuma,
            firstDiagnostic = first.Diagnostic,
            secondSampleCount = second.SampleCount,
            secondNonBlackSampleCount = second.NonBlackSampleCount,
            secondAverageLuma = second.AverageLuma,
            secondDiagnostic = second.Diagnostic,
            diff.ComparedSamples,
            diff.ChangedSamples,
            diff.AverageAbsoluteRgbDelta,
            diff.HasMovement
        };
        Console.WriteLine($"DESKTOP_PROBE={JsonSerializer.Serialize(result, JsonOptions)}");
        return NativeWindowDiagnostics.IsVisible(hwnd) && attachValid && rectValid && first.HasNonBlackPixels && diff.HasMovement ? 0 : 31;
    }

    private static async Task<int> RunWallpaperOnlyAsync(DiagnosticOptions options, Go2RtcProcessManager? relay)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        await using var rendererManager = new RendererProcessManager(loggerFactory.CreateLogger<RendererProcessManager>());
        try
        {
            var monitor = new DesktopMonitorProvider().GetMonitors().FirstOrDefault();
            if (monitor is null)
            {
                Console.Error.WriteLine("WALLPAPER_RESULT=FAILED code=NO_MONITOR");
                return 11;
            }

            var wallpaperResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RendererMetrics? visibleMetrics = null;
            rendererManager.RendererEventReceived += (_, rendererEvent) =>
            {
                var metrics = rendererEvent.Metrics is { } m
                    ? $" metrics=state:{m.MediaState},vout:{m.VoutCount},time:{m.MediaTimeMs},age:{m.VideoProgressAgeSeconds:0.0}s,reconnect:{m.ReconnectCount},visible:{m.WindowVisible},class:{m.WindowClass},hwnd:0x{m.RendererHwnd.ToInt64():X},parent:0x{m.ParentHwnd.ToInt64():X},expectedParent:0x{m.ExpectedParentHwnd.ToInt64():X},rect:{m.RendererRect},monitor:{m.MonitorRect},decoded:{m.Presentation?.DecodedFrameCount},paint:{m.Presentation?.PaintCount},presented:{m.Presentation?.PresentedFrameCount},checksum:0x{m.Presentation?.LastPresentedChecksum:X}"
                    : string.Empty;
                Console.Error.WriteLine($"[renderer] {rendererEvent.Type} code={rendererEvent.ErrorCode ?? "-"} message={rendererEvent.UserMessage ?? "-"}{metrics} details={rendererEvent.TechnicalDetails ?? "-"}");
                if (rendererEvent.Type == RendererEventType.WallpaperEndToEndVerified)
                {
                    visibleMetrics = rendererEvent.Metrics;
                    wallpaperResult.TrySetResult(true);
                }
                else if (rendererEvent.Type is RendererEventType.FatalError or RendererEventType.PlaybackError or RendererEventType.AttachmentFailed)
                {
                    wallpaperResult.TrySetResult(false);
                }
            };

            var startOptions = new RendererStartOptions(options.Url, options.UserName, options.Password,
                options.Transport, options.CacheMs, DisplayMode.Fill, monitor.PersistentId, Environment.ProcessId,
                options.HardwareDecode, true, options.RenderTestPattern);
            await rendererManager.StartAsync(startOptions).WaitAsync(TimeSpan.FromSeconds(15));
            var wallpaperSucceeded = await wallpaperResult.Task.WaitAsync(TimeSpan.FromSeconds(120));
            Console.WriteLine($"WALLPAPER_RESULT={(wallpaperSucceeded ? "SUCCESS" : "FAILED")}");
            if (wallpaperSucceeded && options.DesktopProbe && visibleMetrics is { } metrics)
            {
                var first = DesktopPixelProbe.Sample(metrics.RendererRect);
                await Task.Delay(TimeSpan.FromSeconds(1));
                var second = DesktopPixelProbe.Sample(metrics.RendererRect);
                var diff = DesktopPixelProbe.Compare(first, second);
                Console.WriteLine($"DESKTOP_PROBE={JsonSerializer.Serialize(new { rendererHwnd = metrics.RendererHwnd.ToInt64(), parentHwnd = metrics.ParentHwnd.ToInt64(), expectedParentHwnd = metrics.ExpectedParentHwnd.ToInt64(), metrics.RendererRect, metrics.MonitorRect, metrics.WindowVisible, metrics.WindowClass, firstSampleCount = first.SampleCount, firstNonBlackSampleCount = first.NonBlackSampleCount, firstAverageLuma = first.AverageLuma, firstDiagnostic = first.Diagnostic, secondSampleCount = second.SampleCount, secondNonBlackSampleCount = second.NonBlackSampleCount, secondAverageLuma = second.AverageLuma, secondDiagnostic = second.Diagnostic, diff.ComparedSamples, diff.ChangedSamples, diff.AverageAbsoluteRgbDelta, diff.HasMovement }, JsonOptions)}");
            }
            if (wallpaperSucceeded && options.HoldSeconds > 0)
            {
                Console.WriteLine($"WALLPAPER_HOLD_SECONDS={options.HoldSeconds}");
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
            }

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
                Console.Error.WriteLine($"[renderer] {rendererEvent.Type} code={rendererEvent.ErrorCode ?? "-"} details={rendererEvent.TechnicalDetails ?? "-"}");
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
        int TimeoutSeconds, int CacheMs, HardwareDecodeMode HardwareDecode, bool StartGo2Rtc, bool RenderTestPattern, bool Wallpaper, bool WallpaperOnly, bool IpcSmoke,
        bool DesktopProbe, nint? ProbeHwnd, int ProbeSeconds, int HoldSeconds, string? StartupAction, string? StartupExecutable, bool CreateDiagnosticsPackage, bool ShowHelp);
}
