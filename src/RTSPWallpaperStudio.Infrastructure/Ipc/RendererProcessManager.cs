using System.Diagnostics;
using System.Globalization;
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
    private readonly object _sessionsLock = new();

    public RendererProcessManager(ILogger<RendererProcessManager> logger)
    {
        _logger = logger;
    }

    public event EventHandler<RendererEvent>? RendererEventReceived;

    public async Task StartAsync(RendererStartOptions options, CancellationToken cancellationToken = default)
    {
        var pipeName = $"RTSPWallpaperStudio-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var rendererId = Guid.NewGuid().ToString("N");
        var rendererPath = ResolveRendererPath();
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = rendererPath,
            Arguments = string.Join(' ',
                $"--pipe \"{pipeName}\"",
                $"--parent-pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}",
                $"--monitor-id \"{options.MonitorId}\"",
                $"--renderer-id \"{rendererId}\""),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
        if (process is null)
        {
            await server.DisposeAsync();
            throw new InvalidOperationException("Rendererプロセスを起動できませんでした。");
        }

        var job = new WindowsJobObject(_logger);
        var session = new RendererSession(process, server, job, rendererId, _logger, PublishEvent);
        lock (_sessionsLock)
        {
            _sessions.Add(session);
        }

        try
        {
            job.Assign(process);
            await session.ConnectAsync(cancellationToken);
            session.StartEventLoop(cancellationToken);
            await session.SendAsync(new IpcMessage(
                IpcMessageKind.Command,
                "start",
                JsonSerializer.Serialize(options)), cancellationToken);
            _logger.LogInformation("Rendererを起動しました。PID={Pid} Monitor={MonitorId} RendererId={RendererId}",
                process.Id, options.MonitorId, rendererId);
        }
        catch
        {
            lock (_sessionsLock)
            {
                _sessions.Remove(session);
            }

            await session.DisposeAsync();
            throw;
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        RendererSession[] sessions;
        lock (_sessionsLock)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                await session.SendAsync(new IpcMessage(IpcMessageKind.Command, "stop"), cancellationToken);
                await session.WaitForExitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Renderer終了処理でエラーが発生しました。PID={Pid}", session.Process.Id);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
    }

    public async Task SendCommandAsync(string name, string? payload = null, CancellationToken cancellationToken = default)
    {
        RendererSession[] sessions;
        lock (_sessionsLock)
        {
            sessions = _sessions.ToArray();
        }

        foreach (var session in sessions)
        {
            await session.SendAsync(new IpcMessage(IpcMessageKind.Command, name, payload), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();

    private void PublishEvent(RendererEvent rendererEvent)
    {
        _logger.LogInformation("Renderer event {Type} ({ErrorCode}) id={RendererId}",
            rendererEvent.Type, rendererEvent.ErrorCode ?? "-", rendererEvent.RendererId);
        RendererEventReceived?.Invoke(this, rendererEvent);
    }

    private static string ResolveRendererPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "RTSPWallpaperStudio.Renderer.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RTSPWallpaperStudio.Renderer", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "RTSPWallpaperStudio.Renderer.exe"));
        return File.Exists(candidate) ? candidate : throw new FileNotFoundException("Renderer実行ファイルが見つかりません。Portable配置では同じフォルダーへ配置してください。", candidate);
    }

    private sealed class RendererSession : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly WindowsJobObject _job;
        private readonly ILogger _logger;
        private readonly Action<RendererEvent> _eventSink;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _eventLoop;

        public RendererSession(Process process, NamedPipeServerStream pipe, WindowsJobObject job, string rendererId,
            ILogger logger, Action<RendererEvent> eventSink)
        {
            Process = process;
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            _job = job;
            RendererId = rendererId;
            _logger = logger;
            _eventSink = eventSink;
        }

        public Process Process { get; }
        public string RendererId { get; }

        public Task ConnectAsync(CancellationToken cancellationToken) => _pipe.WaitForConnectionAsync(cancellationToken);

        public void StartEventLoop(CancellationToken cancellationToken)
        {
            _eventLoop = ReadEventsAsync(cancellationToken);
        }

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(IpcProtocol.Serialize(message)).WaitAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await Process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Rendererが終了しないためJob Objectで回収します。PID={Pid}", Process.Id);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _pipe.Dispose();
            if (!Process.HasExited)
            {
                try
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Rendererプロセスの終了待機を省略しました。PID={Pid}", Process.Id);
                }
            }

            if (_eventLoop is not null)
            {
                try { await _eventLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
            }

            _writer.Dispose();
            _reader.Dispose();
            _writeLock.Dispose();
            _lifetime.Dispose();
            _job.Dispose();
            Process.Dispose();
        }

        private async Task ReadEventsAsync(CancellationToken externalCancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, _lifetime.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(linked.Token);
                    if (line is null)
                    {
                        return;
                    }

                    if (!IpcProtocol.TryDeserializeMessage(line, out var message) || message is null || message.Kind != IpcMessageKind.Event || string.IsNullOrWhiteSpace(message.Payload))
                    {
                        continue;
                    }

                    var rendererEvent = IpcProtocol.DeserializePayload<RendererEvent>(message.Payload);
                    if (rendererEvent is not null)
                    {
                        _eventSink(rendererEvent);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Renderer IPCイベントループを終了しました。PID={Pid}", Process.Id);
            }
        }
    }
}
