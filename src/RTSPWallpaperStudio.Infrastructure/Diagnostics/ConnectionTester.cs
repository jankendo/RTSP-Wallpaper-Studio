using System.Diagnostics;
using System.Net.Sockets;
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

        if (!Uri.TryCreate(parts.Url, UriKind.Absolute, out var uri))
        {
            return new ConnectionTestResult(false, "RTSP URLが正しくありません。", "URL解析に失敗しました。", stopwatch.Elapsed);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            var local = client.Client.LocalEndPoint?.ToString() ?? "不明";
            return new ConnectionTestResult(true, "TCP接続に成功しました。", $"ホスト：{uri.Host}:{uri.Port}\nローカルエンドポイント：{local}\nRTSPオープンはRendererで実行します。", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            var message = ex is OperationCanceledException ? "接続がタイムアウトしました。" : "RTSPサーバーへ接続できませんでした。";
            return new ConnectionTestResult(false, message, "URL、配信ソフト、Windowsファイアウォールを確認してください。", stopwatch.Elapsed);
        }
    }
}
