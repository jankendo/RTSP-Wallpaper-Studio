using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.Renderer;

public partial class RendererWindow : Window
{
    private readonly string _pipeName;
    private readonly string _monitorId;
    private readonly int _parentProcessId;
    private readonly WorkerWLocator _workerWLocator = new();
    private readonly DesktopMonitorProvider _monitorProvider = new();
    private readonly RendererPipeServer _pipeServer;
    private readonly RendererPlayback _playback;
    private readonly DispatcherTimer _healthTimer;
    private MonitorInfo? _monitor;
    private bool _closing;

    public RendererWindow(string pipeName, string monitorId, int parentProcessId)
    {
        InitializeComponent();
        _pipeName = pipeName;
        _monitorId = monitorId;
        _parentProcessId = parentProcessId;
        _pipeServer = new RendererPipeServer(_pipeName);
        _pipeServer.MessageReceived += OnMessageReceivedAsync;
        _playback = new RendererPlayback(VideoView, OnPlaybackError);
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += OnHealthTimer;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _monitor = _monitorProvider.GetMonitors().FirstOrDefault(x => x.PersistentId.Equals(_monitorId, StringComparison.OrdinalIgnoreCase))
                   ?? _monitorProvider.GetMonitors().FirstOrDefault();
        if (_monitor is not null)
        {
            Left = _monitor.Bounds.X;
            Top = _monitor.Bounds.Y;
            Width = _monitor.Bounds.Width;
            Height = _monitor.Bounds.Height;
        }

        _healthTimer.Start();
        await _pipeServer.StartAsync(CancellationToken.None);
    }

    private async void OnHealthTimer(object? sender, EventArgs e)
    {
        if (_parentProcessId > 0 && !IsProcessAlive(_parentProcessId))
        {
            Close();
            return;
        }

        if (_monitor is not null && !_workerWLocator.IsAttached(new WindowInteropHelper(this).Handle))
        {
            _workerWLocator.TryAttach(new WindowInteropHelper(this).Handle, _monitor, out _);
        }

        await Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(IpcEnvelope message)
    {
        switch (message.Command.ToLowerInvariant())
        {
            case "start" when message.Payload is not null:
                var options = JsonSerializer.Deserialize<RendererStartOptions>(message.Payload);
                if (options is not null)
                {
                    await StartPlaybackAsync(options);
                }
                break;
            case "stop":
                await _playback.StopAsync();
                Close();
                break;
            case "reconnect":
                await _playback.ReconnectAsync();
                break;
        }
    }

    private async Task StartPlaybackAsync(RendererStartOptions options)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        _monitor ??= _monitorProvider.GetMonitors().FirstOrDefault(x => x.PersistentId.Equals(options.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (_monitor is null)
        {
            OnPlaybackError("対象ディスプレイが見つかりません。");
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (!_workerWLocator.TryAttach(handle, _monitor, out var diagnostic))
        {
            OnPlaybackError($"WorkerWへの配置に失敗しました。{diagnostic}");
            return;
        }

        await _playback.StartAsync(options);
    }

    private void OnPlaybackError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _healthTimer.Stop();
        _pipeServer.Dispose();
        _playback.Dispose();
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
}

internal sealed class RendererPipeServer : IDisposable
{
    private readonly string _pipeName;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public RendererPipeServer(string pipeName) => _pipeName = pipeName;

    public event Func<IpcEnvelope, Task>? MessageReceived;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => AcceptLoopAsync(_stop.Token), _stop.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is not null && IpcProtocol.TryDeserialize(line, out var message) && message is not null && MessageReceived is not null)
                {
                    await MessageReceived(message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        _stop?.Dispose();
    }
}
