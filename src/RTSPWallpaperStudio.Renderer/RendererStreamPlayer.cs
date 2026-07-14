using System.Text.Json;
using LibVLCSharp.Shared;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.Renderer;

internal sealed class RendererStreamPlayer : IAsyncDisposable
{
    private readonly NativeRendererWindow _window;
    private readonly DesktopHostController _desktopHost;
    private readonly Func<RendererEvent, Task> _report;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private RendererStartOptions? _lastOptions;
    private string? _lastMediaError;
    private int _reconnectCount;

    public RendererStreamPlayer(NativeRendererWindow window, DesktopHostController desktopHost, Func<RendererEvent, Task> report)
    {
        _window = window;
        _desktopHost = desktopHost;
        _report = report;
    }

    public bool IsRunning => _player is not null;
    public bool IsAttached { get; private set; }

    public async Task<bool> StartAsync(RendererStartOptions options, bool recoverShell = false, CancellationToken cancellationToken = default)
    {
        _lastOptions = options;
        _window.Hide();
        if (recoverShell)
        {
            _window.ResetToHiddenTopLevel();
            _desktopHost.Invalidate();
        }

        await StopPlayerAsync();
        IsAttached = false;
        _lastMediaError = null;
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            await ReportAsync(RendererEventType.LibVlcInitialized);
            _libVlc = new LibVLC("--no-video-title-show", "--no-audio", "--quiet");
            _player = new MediaPlayer(_libVlc)
            {
                Hwnd = _window.Hwnd,
                Mute = true
            };
            _player.EncounteredError += OnEncounteredError;
            _player.Buffering += OnBuffering;

            await ReportAsync(RendererEventType.StreamOpening);
            var playbackOptions = new RtspPlaybackOptions(options.Url, options.UserName, options.Password,
                options.Transport, options.NetworkCachingMs, options.HardwareDecode, options.MuteAudio);
            var location = RtspLocationBuilder.Build(playbackOptions.Url, playbackOptions.UserName, playbackOptions.Password);
            _media = new Media(_libVlc, location, FromType.FromLocation);
            foreach (var option in RtspPlaybackOptionsFactory.CreateMediaOptions(playbackOptions))
            {
                _media.AddOption(option);
            }

            if (!_player.Play(_media))
            {
                return await FailAsync(RendererErrorCodes.RtspOpenFailed,
                    "RTSP映像を開始できませんでした。", "MediaPlayer.Playがfalseを返しました。", cancellationToken);
            }

            var outputReady = await WaitForVideoOutputAsync(cancellationToken);
            if (!outputReady)
            {
                var code = string.IsNullOrWhiteSpace(_lastMediaError) ? RendererErrorCodes.RtspFirstFrameTimeout : RendererErrorCodes.RtspOpenFailed;
                var message = code == RendererErrorCodes.RtspFirstFrameTimeout
                    ? "最初の映像出力を確認できませんでした。Rendererは表示していません。"
                    : "RTSPストリームで再生エラーが発生しました。";
                return await FailAsync(code, message, _lastMediaError ?? "15秒以内にPlayingかつVoutCount>0になりませんでした。", cancellationToken);
            }

            await ReportAsync(RendererEventType.MediaParsed, userMessage: "LibVLCのメディア解析が完了しました。");
            await ReportAsync(RendererEventType.VideoTrackDetected, userMessage: "映像トラックを検出しました。");
            await ReportAsync(RendererEventType.VideoOutputReady, userMessage: "最初の映像出力を検出しました。壁紙配置を開始します。", metrics: BuildMetrics(null));
            var attach = _desktopHost.Attach(_window.Hwnd, FindMonitor(options.MonitorId));
            if (!attach.Success)
            {
                await ReportAsync(RendererEventType.AttachmentFailed, attach.ErrorCode, attach.UserMessage, attach.TechnicalDetails);
                await StopPlayerAsync();
                _window.ResetToHiddenTopLevel();
                return false;
            }

            await ReportAsync(RendererEventType.DesktopHostDiscovered, userMessage: attach.Discovery?.Diagnostic,
                technicalDetails: attach.Discovery?.Diagnostic);
            await ReportAsync(RendererEventType.AttachmentSucceeded, metrics: BuildMetrics(attach.Discovery));
            if (!_desktopHost.ValidateAttachment(_window.Hwnd, out var validationDiagnostic))
            {
                return await FailAsync(RendererErrorCodes.WallpaperParentMismatch,
                    "壁紙配置の最終検証に失敗しました。Rendererは表示していません。", validationDiagnostic, cancellationToken);
            }

            _window.ShowAfterValidation();
            IsAttached = true;
            await ReportAsync(RendererEventType.WallpaperVisible, userMessage: "壁紙を表示しました。", metrics: BuildMetrics(attach.Discovery));
            await ReportAsync(RendererEventType.PlaybackRunning, userMessage: "再生中です。", metrics: BuildMetrics(attach.Discovery));
            return true;
        }
        catch (Exception ex)
        {
            await FailAsync(RendererErrorCodes.VlcInitFailed, "Rendererを初期化できませんでした。", ex.ToString(), cancellationToken);
            return false;
        }
    }

    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_lastOptions is null)
        {
            return false;
        }

        _reconnectCount++;
        await ReportAsync(RendererEventType.Reconnecting, userMessage: "RTSPを再接続しています。");
        return await StartAsync(_lastOptions, recoverShell: false, cancellationToken);
    }

    public async Task<bool> ReattachAfterShellRestartAsync(CancellationToken cancellationToken = default)
    {
        if (_lastOptions is null)
        {
            return false;
        }

        await ReportAsync(RendererEventType.ExplorerRestartDetected, userMessage: "Explorerの変更を検出しました。壁紙を安全に再配置します。");
        _reconnectCount++;
        var restored = await StartAsync(_lastOptions, recoverShell: true, cancellationToken);
        if (restored)
        {
            await ReportAsync(RendererEventType.Reattached, userMessage: "デスクトップホストへ再配置しました。");
        }

        return restored;
    }

    public async Task StopAsync()
    {
        _window.Hide();
        await StopPlayerAsync();
        await ReportAsync(RendererEventType.Stopped, userMessage: "Rendererを停止しました。");
    }

    public async ValueTask DisposeAsync()
    {
        await StopPlayerAsync();
        _libVlc?.Dispose();
        _libVlc = null;
    }

    private async Task<bool> WaitForVideoOutputAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        var consecutive = 0;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_player is null)
            {
                return false;
            }

            if (_player.State == VLCState.Playing && _player.VoutCount > 0)
            {
                consecutive++;
                if (consecutive >= 3)
                {
                    return true;
                }
            }
            else
            {
                consecutive = 0;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private async Task<bool> FailAsync(string errorCode, string userMessage, string details, CancellationToken cancellationToken)
    {
        await ReportAsync(RendererEventType.FatalError, errorCode, userMessage, details);
        await StopPlayerAsync();
        _window.ResetToHiddenTopLevel();
        return false;
    }

    private async Task StopPlayerAsync()
    {
        IsAttached = false;
        if (_player is not null)
        {
            _player.EncounteredError -= OnEncounteredError;
            _player.Buffering -= OnBuffering;
            _player.Stop();
            _player.Dispose();
            _player = null;
        }

        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;
        await Task.CompletedTask;
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        _lastMediaError = "LibVLC EncounteredError";
        _ = ReportAsync(RendererEventType.PlaybackError, RendererErrorCodes.RtspOpenFailed,
            "LibVLCがRTSP再生エラーを通知しました。", _lastMediaError);
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e) =>
        _ = ReportAsync(RendererEventType.Buffering, userMessage: "RTSP映像をバッファリングしています。");

    private MonitorInfo FindMonitor(string monitorId)
    {
        var monitors = new DesktopMonitorProvider().GetMonitors();
        return monitors.FirstOrDefault(x => x.PersistentId.Equals(monitorId, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault()
               ?? throw new InvalidOperationException("対象ディスプレイが見つかりません。");
    }

    private RendererMetrics? BuildMetrics(DesktopHostDiscoveryResult? discovery)
    {
        if (discovery is null)
        {
            return null;
        }

        var parent = DesktopWindowDiagnostics.GetParent(_window.Hwnd);
        var rect = DesktopWindowDiagnostics.TryGetScreenRect(_window.Hwnd, out var screenRect)
            ? screenRect
            : new RectD();
        return new RendererMetrics(Environment.ProcessId, _window.Hwnd, parent, discovery.HostHwnd,
            unchecked((int)(_player?.VoutCount ?? 0u)), _player?.State.ToString() ?? "Stopped", null, null, null,
            _reconnectCount, discovery.Strategy, rect, rect);
    }

    private Task ReportAsync(RendererEventType type, string? errorCode = null, string? userMessage = null,
        string? technicalDetails = null, RendererMetrics? metrics = null) =>
        _report(new RendererEvent(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), type, DateTimeOffset.UtcNow,
            errorCode, userMessage, technicalDetails, metrics));

}
