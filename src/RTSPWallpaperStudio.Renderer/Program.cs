using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.Renderer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
            RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        Trace("RunAsync開始");
        var commandLine = RendererCommandLine.Parse(args);
        Trace($"引数解析完了 pipe={commandLine.PipeName}");
        if (string.IsNullOrWhiteSpace(commandLine.PipeName))
        {
            Environment.ExitCode = 2;
            return;
        }

        using var lifetime = new CancellationTokenSource();
        Trace("Rendererウィンドウ作成前");
        using var window = new NativeRendererWindow();
        Trace($"Rendererウィンドウ作成完了 hwnd=0x{window.Hwnd.ToInt64():X}");
        await using var ipc = new RendererIpcClient(commandLine.PipeName);
        Trace("IPC接続前");
        await ipc.ConnectAsync(lifetime.Token);
        Trace("IPC接続完了");
        var desktopHost = new DesktopHostController(new DesktopHostDiscovery());
        await using var player = new RendererStreamPlayer(window, desktopHost, rendererEvent => ipc.SendEventAsync(rendererEvent, lifetime.Token));

        Trace("RendererReady送信前");
        await ipc.SendEventAsync(new RendererEvent(commandLine.RendererId, RendererEventType.RendererReady, DateTimeOffset.UtcNow,
            UserMessage: "Rendererを起動しました。"), lifetime.Token);
        Trace("RendererReady送信完了");
        await ipc.SendEventAsync(new RendererEvent(commandLine.RendererId, RendererEventType.RendererWindowCreated,
            DateTimeOffset.UtcNow, UserMessage: "Renderer専用HWNDを作成しました。",
            TechnicalDetails: $"hwnd=0x{window.Hwnd.ToInt64():X}; class={NativeRendererWindow.ClassName}; processId={Environment.ProcessId}"), lifetime.Token);

        Trace("コマンドループ開始");
        var commandLoop = ipc.RunCommandLoopAsync(async message =>
        {
            Trace($"コマンド受信 name={message.Name}");
            switch (message.Name.ToLowerInvariant())
            {
                case "start" when message.Payload is not null:
                    var options = JsonSerializer.Deserialize<RendererStartOptions>(message.Payload);
                    if (options is not null)
                    {
                        await player.StartAsync(options, cancellationToken: lifetime.Token);
                    }
                    break;
                case "stop":
                    await player.StopAsync();
                    lifetime.Cancel();
                    window.CloseFromAnyThread();
                    break;
                case "reconnect":
                    await player.ReconnectAsync(lifetime.Token);
                    break;
                case "reset-shell":
                    await player.ReattachAfterShellRestartAsync(lifetime.Token);
                    break;
            }
        }, lifetime.Token);

        var watchdog = WatchdogAsync(commandLine.ParentProcessId, commandLine.RendererId, player, window, desktopHost, ipc, lifetime.Token);
        NativeRendererWindow.RunMessageLoop();
        Trace("メッセージループ終了");
        lifetime.Cancel();
        await Task.WhenAny(commandLoop, watchdog);
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("RTSP_WALLPAPER_IPC_TRACE"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[renderer-trace] {message}");
        }
    }

    private static async Task WatchdogAsync(int parentPid, string rendererId, RendererStreamPlayer player, NativeRendererWindow window,
        DesktopHostController desktopHost, RendererIpcClient ipc, CancellationToken cancellationToken)
    {
        var stallThreshold = TimeSpan.FromSeconds(8);
        var unhealthySince = (DateTimeOffset?)null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            if (parentPid > 0 && !IsProcessAlive(parentPid))
            {
                await player.StopAsync();
                window.CloseFromAnyThread();
                return;
            }

            var heartbeatMetrics = player.IsRunning && player.IsAttached
                ? player.GetMetricsForDiagnostics()
                : null;
            await ipc.SendEventAsync(new RendererEvent(rendererId, RendererEventType.Heartbeat, DateTimeOffset.UtcNow,
                Metrics: heartbeatMetrics), cancellationToken);
            if (player.IsRunning && player.IsAttached && !desktopHost.ValidateAttachment(window.Hwnd, out _))
            {
                await player.ReattachAfterShellRestartAsync(cancellationToken);
                continue;
            }

            if (player.IsRunning && player.IsAttached)
            {
                var health = player.GetPlaybackHealth();
                var now = DateTimeOffset.UtcNow;
                var unhealthy = player.HasPlaybackError || !health.IsPlaying || health.VoutCount <= 0;
                if (unhealthy)
                {
                    unhealthySince ??= now;
                    if (now - unhealthySince.Value >= TimeSpan.FromSeconds(4))
                    {
                        await player.ReconnectAsync(cancellationToken);
                        unhealthySince = null;
                    }
                }
                else
                {
                    unhealthySince = null;
                    if (PlaybackStallDetector.IsStalled(health.IsPlaying, health.VoutCount, health.LastProgressAt,
                        now, stallThreshold))
                    {
                        await player.RecoverFromStallAsync(stallThreshold, cancellationToken);
                    }
                }
            }
            else
            {
                unhealthySince = null;
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record RendererCommandLine(string PipeName, string MonitorId, int ParentProcessId, string RendererId)
    {
        public static RendererCommandLine Parse(string[] args)
        {
            string Get(string key) => args.SkipWhile(x => !x.Equals(key, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? string.Empty;
            var pid = int.TryParse(Get("--parent-pid"), out var parsed) ? parsed : -1;
            var rendererId = Get("--renderer-id");
            return new RendererCommandLine(Get("--pipe"), Get("--monitor-id"), pid, string.IsNullOrWhiteSpace(rendererId) ? Guid.NewGuid().ToString("N") : rendererId);
        }
    }
}
