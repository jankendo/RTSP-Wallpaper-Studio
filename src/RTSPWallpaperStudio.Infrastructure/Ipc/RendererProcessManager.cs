using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.Infrastructure.Ipc;

public sealed class RendererProcessManager : IAsyncDisposable
{
    private readonly ILogger<RendererProcessManager> _logger;
    private readonly List<RendererSession> _sessions = [];

    public RendererProcessManager(ILogger<RendererProcessManager> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(RendererStartOptions options, CancellationToken cancellationToken = default)
    {
        var pipeName = $"RTSPWallpaperStudio-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var rendererPath = ResolveRendererPath();
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = rendererPath,
            Arguments = $"--pipe \"{pipeName}\" --parent-pid {Environment.ProcessId} --monitor-id \"{options.MonitorId}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        }) ?? throw new InvalidOperationException("Rendererプロセスを起動できませんでした。");

        var session = new RendererSession(process, pipeName);
        _sessions.Add(session);
        await session.SendAsync(new IpcEnvelope("start", JsonSerializer.Serialize(options)), cancellationToken);
        _logger.LogInformation("Rendererを起動しました。PID={Pid} Monitor={MonitorId}", process.Id, options.MonitorId);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var session in _sessions.ToArray())
        {
            try
            {
                await session.SendAsync(new IpcEnvelope("stop"), cancellationToken);
                if (!session.Process.WaitForExit(3000))
                {
                    session.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Renderer終了処理でエラーが発生しました。PID={Pid}", session.Process.Id);
            }
            finally
            {
                session.Dispose();
            }
        }

        _sessions.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();

    private static string ResolveRendererPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "RTSPWallpaperStudio.Renderer.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RTSPWallpaperStudio.Renderer", "bin", "x64", "Debug", "net10.0-windows", "RTSPWallpaperStudio.Renderer.exe"));
        return File.Exists(candidate) ? candidate : throw new FileNotFoundException("Renderer実行ファイルが見つかりません。Portable配置では同じフォルダーへ配置してください。", candidate);
    }

    private sealed class RendererSession : IDisposable
    {
        private readonly string _pipeName;
        private NamedPipeClientStream? _pipe;

        public RendererSession(Process process, string pipeName)
        {
            Process = process;
            _pipeName = pipeName;
        }

        public Process Process { get; }

        public async Task SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
        {
            _pipe ??= new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _pipe.ConnectAsync(timeout.Token);
            var bytes = Encoding.UTF8.GetBytes(IpcProtocol.Serialize(envelope) + "\n");
            await _pipe.WriteAsync(bytes, timeout.Token);
            await _pipe.FlushAsync(timeout.Token);
        }

        public void Dispose()
        {
            _pipe?.Dispose();
            Process.Dispose();
        }
    }
}
