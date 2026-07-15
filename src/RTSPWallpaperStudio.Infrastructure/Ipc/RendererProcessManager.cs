using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.Infrastructure.Ipc;

public sealed class RendererProcessManager : IAsyncDisposable
{
    private static readonly Encoding IpcEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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
        var resolution = ResolveRendererPath();
        var rendererPath = resolution.Path;
        var workingDirectory = Path.GetDirectoryName(rendererPath) ?? AppContext.BaseDirectory;
        _logger.LogInformation(
            "RendererExecutableResolved event=RendererExecutableResolved path={Path} source={Source} workingDirectory={WorkingDirectory} metadata={Metadata} candidates={Candidates}",
            rendererPath, resolution.Source, workingDirectory, DescribeExecutable(rendererPath), string.Join(" | ", resolution.Candidates));
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

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
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        });
        if (process is null)
        {
            await server.DisposeAsync();
            throw new InvalidOperationException("Rendererプロセスを起動できませんでした。");
        }

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                _logger.LogWarning("Renderer stderr: {Line}", eventArgs.Data);
            }
        };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                _logger.LogInformation("Renderer stdout: {Line}", eventArgs.Data);
            }
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var job = new WindowsJobObject(_logger);
        var session = new RendererSession(process, server, job, rendererId, rendererPath, workingDirectory, _logger, PublishEvent);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => session.OnProcessExited();
        if (process.HasExited)
        {
            session.OnProcessExited();
        }
        lock (_sessionsLock)
        {
            _sessions.Add(session);
        }

        try
        {
            _logger.LogInformation("Renderer IPC接続を待機します。PID={Pid} Pipe={PipeName}", process.Id, pipeName);
            await session.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            _logger.LogInformation("RendererPipeConnected event=RendererPipeConnected PID={Pid} path={Path}", process.Id, rendererPath);
            // Assign only after the child has connected. This avoids a Windows job
            // boundary interfering with the initial named-pipe handshake.
            job.Assign(process);
            session.StartEventLoop(cancellationToken);
            PublishEvent(new RendererEvent(rendererId, RendererEventType.WallpaperCommandInvoked, DateTimeOffset.UtcNow,
                UserMessage: "壁紙設定コマンドをRendererへ送信します。",
                TechnicalDetails: $"rendererPid={process.Id}; rendererPath={rendererPath}; monitorId={options.MonitorId}; renderTestPattern={options.RenderTestPattern}"));
            await session.SendAsync(new IpcMessage(
                IpcMessageKind.Command,
                "start",
                JsonSerializer.Serialize(options)), cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            _logger.LogInformation("RendererProcessStarted event=RendererProcessStarted PID={Pid} Monitor={MonitorId} RendererId={RendererId} path={Path} workingDirectory={WorkingDirectory}",
                process.Id, options.MonitorId, rendererId, rendererPath, workingDirectory);
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
                session.MarkStopRequested();
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

    private static RendererPathResolution ResolveRendererPath()
    {
        var candidates = new List<string>();
        var local = Path.Combine(AppContext.BaseDirectory, "RTSPWallpaperStudio.Renderer.exe");
        candidates.Add(local);
        if (File.Exists(local))
        {
            return new(local, "app-base", candidates);
        }

        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "renderer", "RTSPWallpaperStudio.Renderer.exe"));
        candidates.Add(sibling);
        if (File.Exists(sibling))
        {
            return new(sibling, "portable-sibling-renderer", candidates);
        }

        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "RTSPWallpaperStudio.Renderer", "bin", "x64"));
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(projectRoot, configuration, "net10.0-windows10.0.19041.0", "RTSPWallpaperStudio.Renderer.exe");
            candidates.Add(candidate);
            if (File.Exists(candidate))
            {
                return new(candidate, $"development-{configuration.ToLowerInvariant()}", candidates);
            }
        }

        throw new FileNotFoundException(
            $"Renderer実行ファイルが見つかりません。確認した候補: {string.Join("; ", candidates)}",
            local);
    }

    private static string DescribeExecutable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return $"fileVersion={version.FileVersion ?? "-"};productVersion={version.ProductVersion ?? "-"};length={info.Length};lastWriteUtc={info.LastWriteTimeUtc:O};sha256={hash}";
        }
        catch (Exception ex)
        {
            return $"metadata-error={ex.GetType().Name}:{ex.Message}";
        }
    }

    private sealed record RendererPathResolution(string Path, string Source, IReadOnlyList<string> Candidates);

    private sealed class RendererSession : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private readonly WindowsJobObject _job;
        private readonly ILogger _logger;
        private readonly Action<RendererEvent> _eventSink;
        private readonly string _rendererPath;
        private readonly string _workingDirectory;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private int _stopRequested;
        private Task? _eventLoop;

        public RendererSession(Process process, NamedPipeServerStream pipe, WindowsJobObject job, string rendererId,
            string rendererPath, string workingDirectory, ILogger logger, Action<RendererEvent> eventSink)
        {
            Process = process;
            _pipe = pipe;
            _job = job;
            RendererId = rendererId;
            _rendererPath = rendererPath;
            _workingDirectory = workingDirectory;
            _logger = logger;
            _eventSink = eventSink;
        }

        public Process Process { get; }
        public string RendererId { get; }

        public void MarkStopRequested() => Interlocked.Exchange(ref _stopRequested, 1);

        public void OnProcessExited()
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                _logger.LogInformation("RendererProcessExited event=RendererProcessExited intentional=true PID={Pid} exitCode={ExitCode}", Process.Id, TryGetExitCode());
                return;
            }

            var exitCode = TryGetExitCode();
            _logger.LogError("RendererProcessExited event=RendererProcessExited unexpected=true PID={Pid} exitCode={ExitCode} path={Path} workingDirectory={WorkingDirectory}",
                Process.Id, exitCode, _rendererPath, _workingDirectory);
            _eventSink(new RendererEvent(RendererId, RendererEventType.FatalError, DateTimeOffset.UtcNow,
                RendererErrorCodes.RendererCrashLoop,
                "Rendererプロセスが予期せず終了しました。壁紙は表示していません。",
                $"event=RendererProcessExited; pid={Process.Id}; exitCode={exitCode}; path={_rendererPath}; workingDirectory={_workingDirectory}"));
        }

        private int TryGetExitCode()
        {
            try { return Process.ExitCode; }
            catch (InvalidOperationException) { return int.MinValue; }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            // Use the synchronous Win32 wait on a worker thread. On some Windows
            // desktop builds WaitForConnectionAsync can remain pending even while
            // a compatible client is already trying to connect.
            var connectionTask = Task.Run(_pipe.WaitForConnection, cancellationToken);
            var exitTask = Process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(connectionTask, exitTask);
            if (completed == exitTask && !_pipe.IsConnected)
            {
                throw new InvalidOperationException($"RendererがIPC接続前に終了しました。PID={Process.Id} exitCode={TryGetExitCode()} path={_rendererPath}");
            }

            await connectionTask;
            _reader = new StreamReader(_pipe, IpcEncoding, leaveOpen: true);
            _writer = new StreamWriter(_pipe, IpcEncoding, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }

        public void StartEventLoop(CancellationToken cancellationToken)
        {
            _eventLoop = ReadEventsAsync(cancellationToken);
        }

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                var writer = _writer ?? throw new InvalidOperationException("Renderer IPC pipe is not connected.");
                await writer.WriteLineAsync(IpcProtocol.Serialize(message)).WaitAsync(cancellationToken);
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
            MarkStopRequested();
            _lifetime.Cancel();
            _writer?.Dispose();
            _reader?.Dispose();
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

            _writeLock.Dispose();
            _lifetime.Dispose();
            _job.Dispose();
            Process.Dispose();
        }

        private async Task ReadEventsAsync(CancellationToken externalCancellationToken)
        {
            var reader = _reader ?? throw new InvalidOperationException("Renderer IPC pipe is not connected.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, _lifetime.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(linked.Token);
                    if (line is null)
                    {
                        return;
                    }

                    if (!IpcProtocol.TryDeserializeMessage(line, out var message) || message is null || message.Kind != IpcMessageKind.Event || string.IsNullOrWhiteSpace(message.Payload))
                    {
                        _logger.LogWarning("Renderer IPCイベントを解釈できません。PID={Pid} RawLength={Length}", Process.Id, line.Length);
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
