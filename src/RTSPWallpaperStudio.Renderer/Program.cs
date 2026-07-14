using System.Diagnostics;
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
        var commandLine = RendererCommandLine.Parse(args);
        if (string.IsNullOrWhiteSpace(commandLine.PipeName))
        {
            Environment.ExitCode = 2;
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var window = new NativeRendererWindow();
        await using var ipc = new RendererIpcClient(commandLine.PipeName);
        await ipc.ConnectAsync(lifetime.Token);
        var desktopHost = new DesktopHostController(new DesktopHostDiscovery());
        await using var player = new RendererStreamPlayer(window, desktopHost, rendererEvent => ipc.SendEventAsync(rendererEvent, lifetime.Token));

        await ipc.SendEventAsync(new RendererEvent(commandLine.RendererId, RendererEventType.RendererReady, DateTimeOffset.UtcNow,
            UserMessage: "Rendererを起動しました。"), lifetime.Token);

        var commandLoop = ipc.RunCommandLoopAsync(async message =>
        {
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
        lifetime.Cancel();
        await Task.WhenAny(commandLoop, watchdog);
    }

    private static async Task WatchdogAsync(int parentPid, string rendererId, RendererStreamPlayer player, NativeRendererWindow window,
        DesktopHostController desktopHost, RendererIpcClient ipc, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            if (parentPid > 0 && !IsProcessAlive(parentPid))
            {
                await player.StopAsync();
                window.CloseFromAnyThread();
                return;
            }

            await ipc.SendEventAsync(new RendererEvent(rendererId, RendererEventType.Heartbeat, DateTimeOffset.UtcNow), cancellationToken);
            if (player.IsRunning && !desktopHost.ValidateAttachment(window.Hwnd, out _))
            {
                await player.ReattachAfterShellRestartAsync(cancellationToken);
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
