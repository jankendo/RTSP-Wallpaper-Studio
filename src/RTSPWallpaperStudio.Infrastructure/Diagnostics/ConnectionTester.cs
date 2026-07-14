using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.Infrastructure.Diagnostics;

public sealed record ConnectionTestResult(
    bool Success,
    string Summary,
    string Details,
    TimeSpan Elapsed,
    string TestId,
    ConnectionTestStage FinalStage,
    string? ErrorCode = null,
    string? ActualTransport = null,
    RtspStreamInformation? Stream = null,
    IReadOnlyList<string>? Steps = null,
    string? TechnicalDetails = null);

public sealed class ConnectionTester
{
    private readonly ILogger<ConnectionTester>? _logger;

    public ConnectionTester(ILogger<ConnectionTester>? logger = null)
    {
        _logger = logger;
    }

    public Task<ConnectionTestResult> TestAsync(string input, int timeoutSeconds, CancellationToken cancellationToken = default) =>
        TestAsync(new ConnectionTestRequest(input, null, null, TransportMode.Tcp, 300,
            timeoutSeconds, HardwareDecodeMode.Automatic), null, cancellationToken);

    public async Task<ConnectionTestResult> TestAsync(
        ConnectionTestRequest request,
        IProgress<ConnectionTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var testId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var steps = new List<string>();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 60));
        var sanitizedUrl = RtspUrlService.SanitizeForLog(request.Url);
        var actualTransport = RtspPlaybackOptionsFactory.DisplayTransport(request.Transport);

        void Report(ConnectionTestStage stage, int percent, string message, string? transport = null)
        {
            var line = $"{stage}: {message}";
            steps.Add(line);
            progress?.Report(new ConnectionTestProgress(stage, percent, message, steps.ToArray(), transport ?? actualTransport));
            _logger?.LogInformation("RTSP test {TestId} stage={Stage} percent={Percent} endpoint={Endpoint} transport={Transport} cacheMs={CacheMs} timeoutSeconds={TimeoutSeconds}",
                testId, stage, percent, sanitizedUrl, transport ?? actualTransport,
                Math.Clamp(request.NetworkCachingMs, 50, 5000), timeout.TotalSeconds);
        }

        Report(ConnectionTestStage.ValidatingUrl, 5, "URLを検証しています。");
        if (!RtspUrlService.TryNormalize(request.Url, out var parts, out var validationError))
        {
            return Failure("RTSP URLを確認できません。", validationError, RendererErrorCodes.RtspOpenFailed,
                ConnectionTestStage.Failed, testId, stopwatch.Elapsed, steps, null, null);
        }

        var username = string.IsNullOrWhiteSpace(request.UserName) ? parts.UserName : request.UserName;
        var password = request.Password ?? parts.Password;
        Report(ConnectionTestStage.ResolvingHost, 12, $"ホスト {parts.Url} を確認しています。");
        if (!Uri.TryCreate(parts.Url, UriKind.Absolute, out var uri))
        {
            return Failure("RTSP URLを確認できません。", "URLの解析に失敗しました。",
                RendererErrorCodes.RtspOpenFailed, ConnectionTestStage.Failed, testId, stopwatch.Elapsed, steps, null, null);
        }

        Report(ConnectionTestStage.CheckingTcpPort, 20, $"TCPポート {uri.Host}:{uri.Port} を確認しています。");
        if (!await IsTcpPortOpenAsync(uri.Host, uri.Port, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false))
        {
            return Failure("RTSPサーバーに接続できません。",
                $"{uri.Host}:{uri.Port} が待ち受けていません。配信ソフト・中継サービス・Windowsファイアウォールを確認してください。",
                RendererErrorCodes.RtspPortClosed, ConnectionTestStage.Failed, testId, stopwatch.Elapsed, steps, null, null);
        }

        Report(ConnectionTestStage.InitializingLibVlc, 30, "LibVLCとnative/pluginsを初期化しています。");
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception ex)
        {
            return Failure("LibVLCを初期化できません。", "native DLLまたはpluginsフォルダーを確認してください。",
                RendererErrorCodes.VlcInitFailed, ConnectionTestStage.Failed, testId, stopwatch.Elapsed, steps, null, ex.ToString());
        }

        var candidates = request.Transport == TransportMode.Automatic
            ? new[] { TransportMode.Tcp, TransportMode.Automatic }
            : new[] { request.Transport };
        Exception? lastException = null;
        var deadline = DateTimeOffset.UtcNow + timeout;
        foreach (var candidate in candidates)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            actualTransport = RtspPlaybackOptionsFactory.DisplayTransport(candidate);
            Report(ConnectionTestStage.OpeningRtsp, 40, "LibVLCでRTSPを開いています。", actualTransport);
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                var opened = await PlayAndWaitAsync(
                    new RtspPlaybackOptions(parts.Url, username, password, candidate,
                        request.NetworkCachingMs, request.HardwareDecode, request.MuteAudio),
                    remaining,
                    Report,
                    cancellationToken).ConfigureAwait(false);

                if (opened.Success)
                {
                    Report(ConnectionTestStage.ReadingMetadata, 92, "映像出力とストリーム情報を読み取っています.", actualTransport);
                    var stream = opened.Stream ?? new RtspStreamInformation(0, null, null, null, null, 0);
                    Report(ConnectionTestStage.Succeeded, 100,
                        $"再生中かつ映像出力あり（VoutCount={stream.VoutCount}）。", actualTransport);
                    return new ConnectionTestResult(true, "RTSP映像を再生確認しました。",
                        $"LibVLC再生成功：映像トラック {stream.VideoTrackCount}本、VoutCount {stream.VoutCount}、接続方式 {actualTransport}。",
                        stopwatch.Elapsed, testId, ConnectionTestStage.Succeeded, null, actualTransport, stream,
                        steps.ToArray(), opened.TechnicalDetails);
                }

                lastException = opened.Exception;
                if (candidate != candidates[^1])
                {
                    steps.Add("Automatic: TCP再生が完了しなかったため、LibVLC自動方式を再試行します。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Report(ConnectionTestStage.Cancelled, 0, "接続テストをキャンセルしました。", actualTransport);
                return Failure("接続テストをキャンセルしました。", "再生・ウィンドウ・LibVLCリソースを解放しました。",
                    "RTSP_TEST_CANCELLED", ConnectionTestStage.Cancelled, testId, stopwatch.Elapsed, steps, actualTransport, null);
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        Report(ConnectionTestStage.Failed, 100, "PlayingかつVoutCount>0を確認できませんでした。", actualTransport);
        var technical = lastException?.ToString() ?? "指定時間内に最初の映像出力が発生しませんでした。";
        return Failure("RTSP映像を再生できません。",
            "TCP接続だけでは成功扱いにせず、LibVLCのPlayingとVoutCount>0を確認しました。URL、認証、配信ソフト、映像トラックを確認してください。",
            RendererErrorCodes.RtspFirstFrameTimeout, ConnectionTestStage.Failed, testId, stopwatch.Elapsed, steps, actualTransport, technical);
    }

    private async Task<(bool Success, RtspStreamInformation? Stream, Exception? Exception, string? TechnicalDetails)> PlayAndWaitAsync(
        RtspPlaybackOptions options,
        TimeSpan timeout,
        Action<ConnectionTestStage, int, string, string?> report,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return (false, null, null, "接続テストの時間制限に達しました。");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var libVlc = new LibVLC("--no-video-title-show", "--quiet");
        using var probeWindow = new NativeConnectionProbeWindow();
        var trace = new List<string>();
        void AddTrace(string value)
        {
            if (trace.Count < 80)
            {
                trace.Add(RedactTechnical(value, options.Url, options.Password));
            }
        }

        void OnLibVlcLog(object? sender, LogEventArgs e) => AddTrace($"libvlc {e.Level} {e.Module}: {e.Message}");
        libVlc.Log += OnLibVlcLog;
        using var player = new MediaPlayer(libVlc)
        {
            Hwnd = probeWindow.Hwnd,
            Mute = options.MuteAudio
        };
        player.Opening += (_, _) => AddTrace("player Opening");
        player.Playing += (_, _) => AddTrace("player Playing");
        player.Vout += (_, _) => AddTrace($"player Vout={player.VoutCount}");
        player.EncounteredError += (_, _) => AddTrace("player EncounteredError");
        var location = RtspLocationBuilder.Build(options.Url, options.UserName, options.Password);
        using var media = new Media(libVlc, location, FromType.FromLocation);
        foreach (var option in RtspPlaybackOptionsFactory.CreateMediaOptions(options))
        {
            media.AddOption(option);
        }

        report(ConnectionTestStage.WaitingForPlaying, 58, "LibVLCのPlaying状態を待っています。", RtspPlaybackOptionsFactory.DisplayTransport(options.Transport));
        if (!player.Play(media))
        {
            libVlc.Log -= OnLibVlcLog;
            return (false, null, null, $"MediaPlayer.Playがfalseを返しました。\n{string.Join("\n", trace)}");
        }

        report(ConnectionTestStage.WaitingForVideoOutput, 75, "PlayingかつVoutCount>0を待っています。", RtspPlaybackOptionsFactory.DisplayTransport(options.Transport));
        var consecutive = 0;
        while (!linked.IsCancellationRequested)
        {
            if (player.State == VLCState.Playing && player.VoutCount > 0)
            {
                consecutive++;
                if (consecutive >= 3)
                {
                    var tracks = media.Tracks;
                    var videoTracks = tracks?.Count(x => x.TrackType == TrackType.Video) ?? 1;
                    return (true,
                        new RtspStreamInformation(videoTracks, null, null, null, player.State.ToString(), unchecked((int)player.VoutCount)),
                        null, $"Playing/Vout gate passed; Parse was not used as the success criterion.\n{string.Join("\n", trace)}");
                }
            }
            else
            {
                consecutive = 0;
            }

            await Task.Delay(250, linked.Token).ConfigureAwait(false);
        }

        libVlc.Log -= OnLibVlcLog;
        return (false, null, null, $"最終状態={player.State}、VoutCount={player.VoutCount}。\n{string.Join("\n", trace)}");
    }

    private static string RedactTechnical(string value, string url, string? password)
    {
        var sanitized = value;
        if (!string.IsNullOrEmpty(password))
        {
            sanitized = sanitized.Replace(password, "***", StringComparison.Ordinal);
        }

        var endpoint = RtspUrlService.SanitizeForLog(url);
        sanitized = Regex.Replace(sanitized, "rtsp(s)?://[^\\s]+", endpoint, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    private static async Task<bool> IsTcpPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static ConnectionTestResult Failure(
        string summary,
        string details,
        string errorCode,
        ConnectionTestStage stage,
        string testId,
        TimeSpan elapsed,
        IReadOnlyList<string> steps,
        string? actualTransport,
        string? technical)
    {
        return new ConnectionTestResult(false, summary, details, elapsed, testId, stage, errorCode,
            actualTransport, null, steps, technical);
    }
}
