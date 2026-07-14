using System.Diagnostics;
using LibVLCSharp.Shared;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.Infrastructure.Diagnostics;

public sealed record ConnectionTestResult(bool Success, string Summary, string Details, TimeSpan Elapsed);

public sealed class ConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(string input, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!RtspUrlService.TryNormalize(input, out var parts, out var validationError))
        {
            return new ConnectionTestResult(false, validationError, "URL形式の検証に失敗しました。", stopwatch.Elapsed);
        }

        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            using var libVlc = new LibVLC("--no-audio", "--quiet", "--no-video-title-show");
            using var media = new Media(libVlc, parts.Url, FromType.FromLocation);
            media.AddOption(":network-caching=300");
            media.AddOption(":rtsp-tcp");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)));

            await media.Parse(MediaParseOptions.ParseNetwork, Math.Clamp(timeoutSeconds * 1000, 1000, 60000), timeout.Token);
            var tracks = media.Tracks;
            var videoTracks = tracks?.Count(x => x.TrackType == TrackType.Video) ?? 0;
            if (!media.IsParsed || videoTracks == 0)
            {
                return new ConnectionTestResult(false, "RTSP映像トラックを確認できませんでした。",
                    "TCP接続だけでは成功扱いにせず、LibVLCのメディア解析で映像トラックが見つかることを確認します。", stopwatch.Elapsed);
            }

            return new ConnectionTestResult(true, "RTSP映像を確認しました。",
                $"LibVLC解析成功：映像トラック {videoTracks}本。壁紙設定時は同じURLをRendererで再生します。", stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new ConnectionTestResult(false, "RTSP解析がタイムアウトしました。",
                "配信開始までの時間、URL、配信ソフト、Windowsファイアウォールを確認してください。", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, "RTSP映像を開けませんでした。",
                $"LibVLCエラー：{ex.Message}", stopwatch.Elapsed);
        }
    }
}
