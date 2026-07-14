using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RTSPWallpaperStudio.Infrastructure.Relay;

public sealed record Go2RtcStartupResult(
    bool Available,
    bool StartedByApplication,
    string Status,
    string? ErrorCode = null,
    string? TechnicalDetails = null);

/// <summary>ローカルgo2rtc中継を安全に起動・確認する。既存プロセスは終了させない。</summary>
public sealed class Go2RtcProcessManager : IAsyncDisposable
{
    public const string DefaultExecutablePath = @"C:\go2rtc\go2rtc.exe";

    private readonly ILogger<Go2RtcProcessManager> _logger;
    private readonly string _executablePath;
    private readonly string _host;
    private readonly int _port;
    private Process? _ownedProcess;

    public Go2RtcProcessManager(ILogger<Go2RtcProcessManager> logger)
        : this(logger, DefaultExecutablePath, "127.0.0.1", 8554)
    {
    }

    public Go2RtcProcessManager(ILogger<Go2RtcProcessManager> logger, string executablePath, string host, int port)
    {
        _logger = logger;
        _executablePath = executablePath;
        _host = host;
        _port = port;
    }

    public bool IsOwnedProcessRunning => _ownedProcess is { HasExited: false };

    public async Task<Go2RtcStartupResult> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (await IsPortOpenAsync(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false))
        {
            var status = IsGo2RtcProcessRunning()
                ? "go2rtcは既に起動しています。"
                : $"{_host}:{_port}は既存プロセスが使用中です。go2rtcの自動起動は不要です。";
            _logger.LogInformation("go2rtc endpoint ready: host={Host} port={Port} owned={Owned}", _host, _port, IsOwnedProcessRunning);
            return new Go2RtcStartupResult(true, false, status);
        }

        if (!File.Exists(_executablePath))
        {
            const string message = "C:\\go2rtc\\go2rtc.exe が見つかりません。RTSP中継なしでもアプリは起動します。";
            _logger.LogWarning("go2rtc executable was not found at configured path");
            return new Go2RtcStartupResult(false, false, message, "GO2RTC_NOT_FOUND", _executablePath);
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
            {
                return new Go2RtcStartupResult(false, false, "go2rtcを起動できませんでした。", "GO2RTC_START_FAILED");
            }

            _ownedProcess = process;
            _logger.LogInformation("Started go2rtc from configured executable; waiting for RTSP port");
            var ready = await WaitForPortAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            if (ready)
            {
                return new Go2RtcStartupResult(true, true, "go2rtcを起動し、RTSPポート8554の待受を確認しました。");
            }

            var exitCode = process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "running-but-port-closed";
            await StopOwnedAsync().ConfigureAwait(false);
            return new Go2RtcStartupResult(false, false, "go2rtcは起動しましたが、RTSPポート8554を確認できませんでした。",
                "GO2RTC_PORT_NOT_READY", exitCode);
        }
        catch (OperationCanceledException)
        {
            await StopOwnedAsync().ConfigureAwait(false);
            return new Go2RtcStartupResult(false, false, "go2rtc起動確認をキャンセルしました。", "GO2RTC_START_CANCELLED");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "go2rtc startup failed");
            await StopOwnedAsync().ConfigureAwait(false);
            return new Go2RtcStartupResult(false, false, "go2rtcの起動に失敗しました。", "GO2RTC_START_FAILED", ex.Message);
        }
    }

    public async Task StopOwnedAsync()
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug(ex, "Owned go2rtc process did not stop cleanly");
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopOwnedAsync().ConfigureAwait(false);

    private async Task<bool> WaitForPortAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsPortOpenAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> IsPortOpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(_host, _port, linked.Token).ConfigureAwait(false);
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

    private static bool IsGo2RtcProcessRunning()
    {
        foreach (var process in Process.GetProcessesByName("go2rtc"))
        {
            try
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}
