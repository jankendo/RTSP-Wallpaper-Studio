using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.Renderer;

internal sealed class RendererStreamPlayer : IAsyncDisposable
{
    private static readonly Regex RtspCredentialPattern = new("(?<scheme>rtsp://)(?<credentials>[^@\\s]+)@", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly NativeRendererWindow _window;
    private readonly DesktopHostController _desktopHost;
    private readonly Func<RendererEvent, Task> _report;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private SoftwareVideoFrameBuffer? _frameBuffer;
    private RendererStartOptions? _lastOptions;
    private DesktopHostDiscoveryResult? _lastDiscovery;
    private MonitorInfo? _lastMonitor;
    private string? _lastMediaError;
    private string? _lastStartFailureDetails;
    private int _reconnectCount;
    private readonly PlaybackProgressTracker _progressTracker = new();
    private readonly List<string> _progressTrace = [];
    private readonly object _progressTraceLock = new();
    private readonly List<string> _libVlcTrace = [];
    private readonly object _libVlcTraceLock = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Stopwatch _frameClock = new();
    private bool _testPatternRunning;
    private DateTimeOffset? _testPatternStartedAt;
    private bool _firstDecodedFrameReported;
    private RendererShellCompositionMetrics? _lastShellComposition;

    public RendererStreamPlayer(NativeRendererWindow window, DesktopHostController desktopHost, Func<RendererEvent, Task> report)
    {
        _window = window;
        _desktopHost = desktopHost;
        _report = report;
    }

    private async Task<bool> StartTestPatternCoreAsync(RendererStartOptions options, bool suppressTransientFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            _lastShellComposition = null;
            _window.ResetPresentationObservation();
            _window.SetTestPattern();
            await ReportAsync(RendererEventType.StreamOpening,
                userMessage: "LibVLCを使わないRenderer内蔵テストパターンを開始しました。",
                technicalDetails: "mode=render-test-pattern; same-window-and-attachment-path=true");

            var discovery = _desktopHost.DiscoverForApply();
            await ReportAsync(RendererEventType.DesktopHostDiscovered,
                userMessage: "デスクトップホストを探索し、候補と選択結果を記録しています。",
                technicalDetails: discovery.Diagnostic);
            var attach = _desktopHost.Attach(_window.Hwnd, _lastMonitor!, discovery);
            if (!attach.Success)
            {
                await ReportAsync(RendererEventType.AttachmentFailed, attach.ErrorCode, attach.UserMessage, attach.TechnicalDetails);
                return await FailStartAsync(attach.ErrorCode, attach.UserMessage, attach.TechnicalDetails,
                    cancellationToken, suppressTransientFailure);
            }

            _lastDiscovery = attach.Discovery;
            await ReportAsync(RendererEventType.AttachmentSucceeded, technicalDetails: attach.TechnicalDetails,
                metrics: BuildMetrics(attach.Discovery));
            await ReportAsync(RendererEventType.RendererAttached,
                userMessage: "Renderer HWNDをデスクトップ配置経路へ接続しました。",
                technicalDetails: attach.TechnicalDetails, metrics: BuildMetrics(attach.Discovery));
            if (!_desktopHost.ValidateAttachment(_window.Hwnd, out var validationDiagnostic))
            {
                return await FailStartAsync(RendererErrorCodes.WallpaperParentMismatch,
                    "壁紙配置の最終検証に失敗しました。Rendererは表示していません。", validationDiagnostic,
                    cancellationToken, suppressTransientFailure);
            }

            await ReportAsync(RendererEventType.RendererBoundsValidated,
                userMessage: "Rendererの親子関係とモニター矩形を検証しました。", metrics: BuildMetrics(attach.Discovery));
            _window.ShowAfterValidation();
            IsAttached = true;
            _testPatternRunning = true;
            _testPatternStartedAt = DateTimeOffset.UtcNow;
            await ReportAsync(RendererEventType.RendererWindowVisibleFlagConfirmed,
                userMessage: "Renderer HWNDの可視状態を確認しました。", metrics: BuildMetrics(attach.Discovery));
            return await VerifyPresentedWallpaperAsync(attach.Discovery!, testPattern: true, cancellationToken);
        }
        catch (Exception ex)
        {
            return await FailStartAsync(RendererErrorCodes.WallpaperPresentationFailed,
                "内蔵テストパターンをデスクトップへ表示できませんでした。", ex.ToString(),
                cancellationToken, suppressTransientFailure);
        }
    }

    private async Task<bool> VerifyPresentedWallpaperAsync(DesktopHostDiscoveryResult discovery, bool testPattern,
        CancellationToken cancellationToken)
    {
        RendererPresentationMetrics presentation;
        try
        {
            presentation = await _window.WaitForPresentationAsync(TimeSpan.FromSeconds(8), cancellationToken);
        }
        catch (TimeoutException)
        {
            return await FailAsync(RendererErrorCodes.WallpaperPresentationFailed,
                "RendererのGDI描画結果を確認できませんでした。壁紙は成功扱いにしません。",
                $"presentation={_window.GetPresentationMetrics()}", cancellationToken);
        }

        if (testPattern)
        {
            await ReportAsync(RendererEventType.RendererSelfTestFrameRendered,
                userMessage: "内蔵テストパターンのGDI描画を確認しました。", metrics: BuildMetrics(discovery));
        }
        else
        {
            await ReportAsync(RendererEventType.RtspFramePaintRequested,
                userMessage: "Renderer HWNDへのGDIペイント要求を確認しました。",
                technicalDetails: $"paintRequests={presentation.PaintRequestCount}; paints={presentation.PaintCount}",
                metrics: BuildMetrics(discovery));
            await ReportAsync(RendererEventType.RtspFramePresented,
                userMessage: "RTSPデコードフレームが同じRenderer HWNDへGDI転送されたことを確認しました。",
                metrics: BuildMetrics(discovery));
        }

        var ownWindowPixels = presentation.PresentedFrameCount > 0 &&
                              presentation.LastPresentedChecksum != 0 &&
                              presentation.LastPaintResult > 0;
        await ReportAsync(RendererEventType.RendererPixelsDetectedOnOwnWindow,
            userMessage: ownWindowPixels ? "Renderer自身の描画結果を検出しました。" : "Renderer自身の描画結果を検出できませんでした。",
            technicalDetails: presentation.ToString(), metrics: BuildMetrics(discovery));

        var rect = _lastMonitor?.Bounds ?? new RectD();
        var first = DesktopPixelProbe.Sample(rect);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var second = DesktopPixelProbe.Sample(rect);
        var diff = DesktopPixelProbe.Compare(first, second);
        var marker = testPattern ? DesktopPixelProbe.ProbeTestPatternMarkers(rect) : null;
        // A child HWND can be clipped by the Shell host when sampled through
        // GetDC(hwnd), so this marker is evidence only. The final decision is
        // based on actual desktop pixels plus the native Shell composition
        // contract below, never on a renderer-only non-black sample.
        var ownMarker = testPattern ? DesktopPixelProbe.ProbeWindowTestPatternMarkers(_window.Hwnd) : null;
        var latestPresentation = _window.GetPresentationMetrics();
        var patternProgressed = latestPresentation.PresentedFrameCount > presentation.PresentedFrameCount &&
                                latestPresentation.LastPresentedChecksum != presentation.LastPresentedChecksum;
        var presentationProgressed = latestPresentation.PresentedFrameCount > presentation.PresentedFrameCount;
        var desktopPixels = first.HasNonBlackPixels;
        var animation = diff.HasMovement;
        // A normal foreground window can cover the sampled desktop region
        // while the renderer itself is still correctly composed behind the
        // Shell. Own-window pixels plus the WorkerW/Shell z-order contract are
        // the authoritative evidence in that case; the desktop sample remains
        // an independent diagnostic signal.
        var shellFrameEvidence = desktopPixels || ownWindowPixels;
        await ReportAsync(RendererEventType.ShellCompositionValidationStarted,
            userMessage: "Windows Shellのアイコン、タスクバー、入力、フォーカスを検証しています。",
            technicalDetails: $"rendererPixels={desktopPixels}; renderer=0x{_window.Hwnd.ToInt64():X}",
            metrics: BuildMetrics(discovery));
        var shellProbe = DesktopShellCompositionProbe.Capture(discovery, _window.Hwnd, shellFrameEvidence);
        _lastShellComposition = shellProbe.Metrics;
        var shell = shellProbe.Metrics;
        var iconsRemainVisible = shell.DesktopIconHostVisible;
        var verified = IsAttached && ownWindowPixels && shell.IsCompositionVerified &&
                       (!testPattern ? presentationProgressed : patternProgressed);

        var enrichedPresentation = latestPresentation with
        {
            OwnWindowPixelsDetected = ownWindowPixels,
            DesktopPixelsDetected = desktopPixels,
            DesktopAnimationDetected = animation,
            TestPatternMarkerDetected = marker?.Detected == true || ownMarker?.Detected == true
        };
        var metrics = (BuildMetrics(discovery) ?? throw new InvalidOperationException("Renderer metricsを作成できません。")) with
        { Presentation = enrichedPresentation };
        await ReportAsync(RendererEventType.RendererPixelsDetectedOnDesktop,
            userMessage: desktopPixels ? "実デスクトップDC上のRenderer領域に画素を検出しました。" : "実デスクトップDC上にRenderer画素を検出できませんでした。",
            technicalDetails: JsonSerializer.Serialize(new { first, second, marker, ownMarker, patternProgressed }), metrics: metrics);
        await ReportAsync(RendererEventType.RendererAnimationDetectedOnDesktop,
            userMessage: animation ? "実デスクトップ上の連続サンプルに変化を検出しました。" : "実デスクトップ上の連続サンプルに変化を検出できませんでした。",
            technicalDetails: JsonSerializer.Serialize(new { diff }), metrics: metrics);
        await ReportAsync(RendererEventType.DesktopIconsRemainVisible,
            userMessage: iconsRemainVisible ? "Shellのアイコンホストを確認しました。" : "Shellのアイコンホストを確認できませんでした。",
            technicalDetails: shell.Diagnostic,
            metrics: metrics);

        await ReportAsync(RendererEventType.DesktopIconHostLocated,
            userMessage: shell.DesktopIconHostLocated ? "デスクトップアイコンのShellホストを特定しました。" : "デスクトップアイコンのShellホストを特定できませんでした。",
            technicalDetails: $"shellView=0x{shell.ShellViewHwnd:X}; sysList=0x{shell.SysListViewHwnd:X}; iconCount={shell.DesktopIconCount}", metrics: metrics);
        await ReportAsync(RendererEventType.DesktopIconHostVisible,
            userMessage: shell.DesktopIconHostVisible ? "デスクトップアイコンホストが可視です。" : "デスクトップアイコンホストが不可視またはCloakedです。",
            technicalDetails: $"shellViewVisible={shell.DesktopIconHostVisible}; iconCount={shell.DesktopIconCount}", metrics: metrics);
        await ReportAsync(RendererEventType.DesktopIconZOrderValidated,
            userMessage: shell.DesktopIconsAboveRenderer ? "デスクトップアイコンがRendererより前面です。" : "Rendererがデスクトップアイコンより前面です。",
            technicalDetails: $"shellViewZ={shell.ShellViewZOrderIndex}; rendererZ={shell.RendererZOrderIndex}; {shell.Diagnostic}", metrics: metrics);
        await ReportAsync(RendererEventType.TaskbarLocated,
            userMessage: shell.TaskbarLocated ? "対象モニターのWindowsタスクバーを特定しました。" : "対象モニターのWindowsタスクバーを特定できませんでした。",
            technicalDetails: $"taskbar=0x{shell.TaskbarHwnd:X}; z={shell.TaskbarZOrderIndex}", metrics: metrics);
        await ReportAsync(RendererEventType.TaskbarVisible,
            userMessage: shell.TaskbarVisible ? "Windowsタスクバーが可視です。" : "Windowsタスクバーが不可視またはCloakedです。",
            technicalDetails: $"taskbar=0x{shell.TaskbarHwnd:X}", metrics: metrics);
        await ReportAsync(RendererEventType.TaskbarZOrderValidated,
            userMessage: shell.TaskbarAboveRenderer ? "WindowsタスクバーがRendererより前面です。" : "RendererがWindowsタスクバーを覆う可能性があります。",
            technicalDetails: $"taskbarZ={shell.TaskbarZOrderIndex}; rendererRoot=0x{shell.RendererRootHwnd:X}", metrics: metrics);
        await ReportAsync(RendererEventType.RendererAltTabVisibilityChecked,
            userMessage: shell.RendererNotInAltTab ? "RendererはAlt+Tab対象外のスタイルです。" : "RendererがAlt+Tab対象になるスタイルです。",
            technicalDetails: $"style=0x{metrics.WindowStyle:X}; exStyle=0x{metrics.ExtendedWindowStyle:X}", metrics: metrics);
        await ReportAsync(RendererEventType.RendererTaskbarVisibilityChecked,
            userMessage: shell.RendererNotInTaskbar ? "Rendererはタスクバーのアプリボタン対象外です。" : "Rendererがタスクバー対象になるスタイルです。",
            technicalDetails: $"exStyle=0x{metrics.ExtendedWindowStyle:X}; taskbar=0x{shell.TaskbarHwnd:X}", metrics: metrics);
        await ReportAsync(RendererEventType.RendererFocusOwnershipChecked,
            userMessage: shell.RendererDoesNotOwnForeground ? "Rendererは前景・アクティブ・フォーカスを取得していません。" : "Rendererがフォーカスを取得しています。",
            technicalDetails: shell.Diagnostic, metrics: metrics);
        await ReportAsync(RendererEventType.DesktopInputHitTestChecked,
            userMessage: shell.DesktopInputAvailable ? "アイコンとタスクバーへのネイティブ入力経路を確認しました。" : "デスクトップ入力経路を確認できませんでした。",
            technicalDetails: $"hit=0x{shell.InputHitTestHwnd:X}; {shell.Diagnostic}", metrics: metrics);

        if (shell.IsCompositionVerified)
        {
            await ReportAsync(RendererEventType.WallpaperShellCompositionVerified,
                userMessage: "Windows Shell合成を検証しました。アイコン、タスクバー、入力、フォーカスの安全条件を満たしています。",
                technicalDetails: JsonSerializer.Serialize(shell), metrics: metrics);
        }
        else
        {
            await ReportAsync(RendererEventType.ShellCompositionValidationFailed,
                RendererErrorCodes.WallpaperShellCompositionValidationFailed,
                GetShellCompositionFailureMessage(shell),
                JsonSerializer.Serialize(shell), metrics);
        }

        if (!verified)
        {
            var errorCode = shell.IsCompositionVerified
                ? RendererErrorCodes.WallpaperEndToEndVerificationFailed
                : RendererErrorCodes.WallpaperShellCompositionValidationFailed;
            return await FailAsync(errorCode,
                shell.IsCompositionVerified
                    ? "壁紙の実描画を最後まで検証できなかったため、成功扱いにしません。"
                    : GetShellCompositionFailureMessage(shell),
                JsonSerializer.Serialize(new { ownWindowPixels, desktopPixels, shellFrameEvidence, animation, marker, ownMarker, patternProgressed, presentationProgressed, iconsRemainVisible, shell, presentation, latestPresentation }),
                cancellationToken);
        }

        await ReportAsync(RendererEventType.WallpaperVisible, userMessage: "壁紙の実描画を確認しました。", metrics: metrics);
        await ReportAsync(RendererEventType.PlaybackRunning, userMessage: "再生中です。", metrics: metrics);
        await ReportAsync(RendererEventType.WallpaperEndToEndVerified,
            userMessage: "Renderer起動、HWND、WorkerW配置、GDI描画、Shell合成、フレーム進行の検証を完了しました。",
            technicalDetails: JsonSerializer.Serialize(new { testPattern, first, second, diff, marker, ownMarker, patternProgressed, presentationProgressed, iconsRemainVisible, shell }), metrics: metrics);
        return true;
    }

    private static string GetShellCompositionFailureMessage(RendererShellCompositionMetrics shell)
    {
        if (!shell.DesktopIconsAboveRenderer)
        {
            return "映像は表示されましたが、Rendererがデスクトップアイコンより前面です。壁紙を停止しました。";
        }

        if (!shell.TaskbarAboveRenderer || !shell.TaskbarVisible)
        {
            return "RendererがWindowsタスクバーを覆う可能性があるため、壁紙を停止しました。";
        }

        if (!shell.DesktopInputAvailable)
        {
            return "デスクトップアイコンまたはタスクバーへの入力経路を確認できないため、壁紙を停止しました。";
        }

        if (!shell.RendererDoesNotOwnForeground)
        {
            return "Rendererがフォーカスを取得したため、壁紙を停止しました。";
        }

        return "Windows Shell合成の安全条件を満たさないため、壁紙を停止しました。";
    }

    public bool IsRunning => _player is not null || _testPatternRunning;
    public bool IsAttached { get; private set; }

    public async Task<bool> StartAsync(RendererStartOptions options, bool recoverShell = false, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _lastStartFailureDetails = null;
            var policy = new ReconnectPolicy();
            var maxAttempts = options.RenderTestPattern ? 1 : 5;
            for (var attempt = 0; attempt < maxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (await StartCoreAsync(options, recoverShell, cancellationToken, suppressTransientFailure: true))
                {
                    return true;
                }

                if (attempt + 1 < maxAttempts)
                {
                    await ReportAsync(RendererEventType.Reconnecting,
                        userMessage: $"RTSP配信元の起動を待って再試行しています（{attempt + 2}/{maxAttempts}）。");
                    await Task.Delay(policy.GetDelay(attempt, jitterFactor: 0.1, randomUnit: 0.5), cancellationToken);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await ReportAsync(RendererEventType.FatalError,
                    options.RenderTestPattern ? RendererErrorCodes.WallpaperEndToEndVerificationFailed : RendererErrorCodes.RtspOpenFailed,
                    options.RenderTestPattern ? "内蔵テストパターンの実デスクトップ検証に失敗しました。" : "RTSP配信元が起動しないため、壁紙を設定できません。",
                    $"試行回数={maxAttempts}; go2rtc、カメラ、URL、認証情報、Renderer画素検証を確認してください。\n最終試行の詳細: {_lastStartFailureDetails ?? "詳細なし"}");
            }

            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<bool> StartCoreAsync(RendererStartOptions options, bool recoverShell, CancellationToken cancellationToken,
        bool suppressTransientFailure = false)
    {
        _lastShellComposition = null;
        _lastOptions = options;
        _window.Hide();
        if (recoverShell)
        {
            _window.ResetToHiddenTopLevel();
            _desktopHost.Invalidate();
        }

        // Size the hidden target before decoding. The same HWND is later
        // attached to WorkerW after the first verified frame.
        _lastMonitor = FindMonitor(options.MonitorId);
        _window.PrepareForPlayback(_lastMonitor.Bounds);

        await StopPlayerAsync();
        IsAttached = false;
        _lastMediaError = null;
        _progressTracker.Reset();
        lock (_progressTraceLock)
        {
            _progressTrace.Clear();
        }
        lock (_libVlcTraceLock)
        {
            _libVlcTrace.Clear();
        }
        _frameClock.Restart();
        _firstDecodedFrameReported = false;
        if (options.RenderTestPattern)
        {
            return await StartTestPatternCoreAsync(options, suppressTransientFailure, cancellationToken);
        }

        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            await ReportAsync(RendererEventType.LibVlcInitialized);
            // The renderer uses LibVLC's software video callbacks. No native
            // HWND vout is created, so the HEVC stream cannot enter the
            // Direct3D11 surface queue that previously deadlocked.
            _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--no-audio", "--vout=vmem", "--avcodec-hw=none");
            _libVlc.Log += OnLibVlcLog;
            _player = new MediaPlayer(_libVlc)
            {
                Mute = true
            };
            _frameBuffer = new SoftwareVideoFrameBuffer(_window.InvalidateVideoFrame, OnFrameDisplayed);
            _window.ResetPresentationObservation();
            _window.SetFrameBuffer(_frameBuffer);
            _frameBuffer.Configure(_player);
            _player.EncounteredError += OnEncounteredError;
            _player.Buffering += OnBuffering;
            _player.TimeChanged += OnTimeChanged;

            await ReportAsync(RendererEventType.StreamOpening);
            // Custom callbacks require CPU-readable frames. This is also the
            // safe fallback for HEVC from the SwitchBot/go2rtc path.
            var playbackOptions = new RtspPlaybackOptions(options.Url, options.UserName, options.Password,
                options.Transport, options.NetworkCachingMs, HardwareDecodeMode.Disabled, options.MuteAudio);
            var location = RtspLocationBuilder.Build(playbackOptions.Url, playbackOptions.UserName, playbackOptions.Password);
            _media = new Media(_libVlc, location, FromType.FromLocation);
            foreach (var option in RtspPlaybackOptionsFactory.CreateMediaOptions(playbackOptions))
            {
                _media.AddOption(option);
            }

            if (!_player.Play(_media))
            {
                return await FailStartAsync(RendererErrorCodes.RtspOpenFailed,
                    "RTSP映像を開始できませんでした。", "MediaPlayer.Playがfalseを返しました。", cancellationToken,
                    suppressTransientFailure);
            }

            var outputReady = await WaitForVideoOutputAsync(cancellationToken);
            if (!outputReady)
            {
                var code = string.IsNullOrWhiteSpace(_lastMediaError) ? RendererErrorCodes.RtspFirstFrameTimeout : RendererErrorCodes.RtspOpenFailed;
                var message = code == RendererErrorCodes.RtspFirstFrameTimeout
                    ? "最初の映像出力を確認できませんでした。Rendererは表示していません。"
                    : "RTSPストリームで再生エラーが発生しました。";
                var health = GetPlaybackHealth();
                return await FailStartAsync(code, message,
                    $"{_lastMediaError ?? "15秒以内に安定した映像進行を確認できませんでした。"} health={health.ToDiagnosticString()} frames={_frameBuffer?.FrameCount ?? 0} frameAt={_frameBuffer?.LastFrameAt?.ToString("O") ?? "none"} trace={GetProgressTrace()} libvlc={GetLibVlcTrace()} framebuffer={_frameBuffer?.LastError ?? "none"}",
                    cancellationToken, suppressTransientFailure);
            }

            await ReportAsync(RendererEventType.MediaParsed, userMessage: "LibVLCのメディア解析が完了しました。");
            await ReportAsync(RendererEventType.VideoTrackDetected, userMessage: "映像トラックを検出しました。");
            _lastMediaError = null;
            await ReportAsync(RendererEventType.VideoOutputReady, userMessage: "最初の映像出力を検出しました。壁紙配置を開始します。", metrics: BuildMetrics(null));
            var discovery = _desktopHost.DiscoverForApply();
            await ReportAsync(RendererEventType.DesktopHostDiscovered,
                userMessage: "デスクトップホストを探索し、候補と選択結果を記録しています.",
                technicalDetails: discovery.Diagnostic);
            var attach = _desktopHost.Attach(_window.Hwnd, _lastMonitor, discovery);
            if (!attach.Success)
            {
                await ReportAsync(RendererEventType.AttachmentFailed, attach.ErrorCode, attach.UserMessage, attach.TechnicalDetails);
                await StopPlayerAsync();
                _window.ResetToHiddenTopLevel();
                return false;
            }

            await ReportAsync(RendererEventType.DesktopHostDiscovered, userMessage: attach.Discovery?.Diagnostic,
                technicalDetails: attach.Discovery?.Diagnostic);
            _lastDiscovery = attach.Discovery;
            await ReportAsync(RendererEventType.AttachmentSucceeded, technicalDetails: attach.TechnicalDetails,
                metrics: BuildMetrics(attach.Discovery));
            await ReportAsync(RendererEventType.RendererAttached,
                userMessage: "Renderer HWNDをデスクトップ配置経路へ接続しました。",
                technicalDetails: attach.TechnicalDetails, metrics: BuildMetrics(attach.Discovery));
            if (!_desktopHost.ValidateAttachment(_window.Hwnd, out var validationDiagnostic))
            {
                return await FailAsync(RendererErrorCodes.WallpaperParentMismatch,
                    "壁紙配置の最終検証に失敗しました。Rendererは表示していません。", validationDiagnostic, cancellationToken);
            }

            await ReportAsync(RendererEventType.RendererBoundsValidated,
                userMessage: "Rendererの親子関係とモニター矩形を検証しました.",
                metrics: BuildMetrics(attach.Discovery));

            _window.ShowAfterValidation();
            IsAttached = true;
            await ReportAsync(RendererEventType.RendererWindowVisibleFlagConfirmed,
                userMessage: "Renderer HWNDの可視状態を確認しました。", metrics: BuildMetrics(attach.Discovery));
            return await VerifyPresentedWallpaperAsync(discovery, testPattern: false, cancellationToken);
        }
        catch (Exception ex)
        {
            await FailStartAsync(RendererErrorCodes.VlcInitFailed, "Rendererを初期化できませんでした。", ex.ToString(), cancellationToken,
                suppressTransientFailure);
            return false;
        }
    }

    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await ReconnectCoreAsync(true, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> ReattachAfterShellRestartAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_lastOptions is null)
            {
                return false;
            }

            await ReportAsync(RendererEventType.ExplorerRestartDetected, userMessage: "Explorerの変更を検出しました。壁紙を安全に再配置します。");
            _reconnectCount++;
            var restored = await StartCoreAsync(_lastOptions, recoverShell: true, cancellationToken);
            if (restored)
            {
                await ReportAsync(RendererEventType.Reattached, userMessage: "デスクトップホストへ再配置しました。");
            }

            return restored;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> RecoverFromStallAsync(TimeSpan threshold, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var health = GetPlaybackHealth();
            if (!PlaybackStallDetector.IsStalled(health.IsPlaying, health.VoutCount, health.LastProgressAt,
                    DateTimeOffset.UtcNow, threshold) || _lastOptions is null)
            {
                return false;
            }

            await ReportAsync(RendererEventType.PlaybackStalled, RendererErrorCodes.RtspPlaybackStalled,
                "映像の進行が停止したため、自動再接続を開始します。", health.ToDiagnosticString(), BuildMetrics(null));
            return await ReconnectCoreAsync(true, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            _window.Hide();
            await StopPlayerAsync();
            await ReportAsync(RendererEventType.Stopped, userMessage: "Rendererを停止しました。");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public bool HasPlaybackError => !string.IsNullOrWhiteSpace(_lastMediaError);

    public async ValueTask DisposeAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            await StopPlayerAsync();
            _libVlc?.Dispose();
            _libVlc = null;
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }
    }

    public PlaybackHealthSnapshot GetPlaybackHealth()
    {
        if (_testPatternRunning)
        {
            var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - (_testPatternStartedAt ?? DateTimeOffset.UtcNow)).TotalMilliseconds);
            _progressTracker.Observe(elapsed, DateTimeOffset.UtcNow);
            var patternProgress = _progressTracker.Snapshot(DateTimeOffset.UtcNow);
            return new PlaybackHealthSnapshot(true, 1, elapsed, patternProgress.LastProgressAt,
                patternProgress.ProgressAge, true, patternProgress.StableSamples, 1);
        }

        var player = _player;
        if (player is not null && _frameBuffer is null)
        {
            try
            {
                ObserveMediaTime(player.Time);
            }
            catch (ObjectDisposedException)
            {
                // A stop/reconnect may dispose MediaPlayer while the watchdog is sampling it.
            }
        }

        var progress = _progressTracker.Snapshot(DateTimeOffset.UtcNow);
        var voutCount = unchecked((int)(player?.VoutCount ?? 0u));
        if (_frameBuffer?.HasFrame == true)
        {
            voutCount = Math.Max(1, voutCount);
        }

        return new PlaybackHealthSnapshot(
            player?.State == VLCState.Playing,
            voutCount,
            progress.MediaTimeMs,
            progress.LastProgressAt,
            progress.ProgressAge,
            progress.IsPrimed,
            progress.StableSamples,
            progress.Rate);
    }

    public RendererMetrics? GetMetricsForDiagnostics() => BuildMetrics(null);

    private async Task<bool> WaitForVideoOutputAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_player is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_lastMediaError) && _player.State != VLCState.Playing)
            {
                return false;
            }

            var health = GetPlaybackHealth();
            if (health.IsPlaying && _frameBuffer?.HasFrame == true && health.IsPrimed)
            {
                return true;
            }
            else
            {
                // VoutCount can become positive before decoded media time is
                // stable. Do not attach the HWND during that transient state.
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

    private async Task<bool> FailStartAsync(string errorCode, string userMessage, string details,
        CancellationToken cancellationToken, bool suppressTransientFailure = false)
    {
        _lastStartFailureDetails = details;
        if (!suppressTransientFailure)
        {
            await ReportAsync(RendererEventType.FatalError, errorCode, userMessage, details);
        }

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
            _player.TimeChanged -= OnTimeChanged;
            _player.Stop();
            _player.Dispose();
            _player = null;
        }

        _window.SetFrameBuffer(null);
        _testPatternRunning = false;
        _testPatternStartedAt = null;
        _frameBuffer?.Dispose();
        _frameBuffer = null;
        _frameClock.Stop();
        _media?.Dispose();
        _media = null;
        if (_libVlc is not null)
        {
            _libVlc.Log -= OnLibVlcLog;
        }
        _libVlc?.Dispose();
        _libVlc = null;
        await Task.CompletedTask;
    }

    private async Task<bool> ReconnectCoreAsync(bool retryUntilRecovered, CancellationToken cancellationToken)
    {
        if (_lastOptions is null)
        {
            return false;
        }

        var policy = new ReconnectPolicy();
        var maxAttempts = retryUntilRecovered ? 6 : 1;
        for (var attempt = 0; attempt < maxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            _reconnectCount++;
            await ReportAsync(RendererEventType.Reconnecting,
                userMessage: attempt == 0
                    ? "RTSPを再接続しています。"
                    : $"RTSP配信元の復帰を待って再接続しています（{attempt + 1}/{maxAttempts}）。");
            if (await StartCoreAsync(_lastOptions, recoverShell: false, cancellationToken, suppressTransientFailure: retryUntilRecovered))
            {
                return true;
            }

            if (attempt + 1 < maxAttempts)
            {
                var delay = policy.GetDelay(attempt, jitterFactor: 0.1, randomUnit: 0.5);
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (retryUntilRecovered && !cancellationToken.IsCancellationRequested)
        {
            await ReportAsync(RendererEventType.FatalError, RendererErrorCodes.RtspOpenFailed,
                "RTSP配信元が復帰しないため、壁紙を表示できません。", "自動再接続を6回実行しました。配信元とgo2rtcの状態を確認してください。");
        }

        return false;
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        _lastMediaError = "LibVLC EncounteredError";
        if (IsAttached)
        {
            // An RTSP camera can briefly report an input error while its
            // transport is being re-established. Do not publish PlaybackError
            // here because the UI treats that event as terminal; the watchdog
            // will perform the bounded automatic reconnect instead.
            _ = ReportAsync(RendererEventType.Buffering,
                userMessage: "RTSP入力が一時停止しました。自動復旧を開始します。",
                technicalDetails: _lastMediaError);
        }
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e) =>
        _ = ReportAsync(RendererEventType.Buffering, userMessage: "RTSP映像をバッファリングしています。");

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_frameBuffer is null)
        {
            ObserveMediaTime(e.Time);
        }
    }

    private void ObserveMediaTime(long mediaTimeMs)
    {
        var accepted = _progressTracker.Observe(mediaTimeMs, DateTimeOffset.UtcNow);
        lock (_progressTraceLock)
        {
            if (_progressTrace.Count < 40)
            {
                _progressTrace.Add($"{mediaTimeMs}ms/{(accepted ? "ok" : "hold")}");
            }
        }
    }

    private void OnFrameDisplayed()
    {
        // Live RTSP streams commonly report a constant MediaPlayer.Time (0).
        // The decoded-frame callback is the authoritative progress signal for
        // this path, measured against a monotonic clock to retain stall and
        // fast-forward protection without depending on live-media timestamps.
        _ = _progressTracker.Observe(_frameClock.ElapsedMilliseconds, DateTimeOffset.UtcNow);
        if (!_firstDecodedFrameReported && _frameBuffer?.FrameCount >= 1)
        {
            _firstDecodedFrameReported = true;
            _ = ReportAsync(RendererEventType.RtspDecodedFrameReceived,
                userMessage: "LibVLCのデコードフレームを受信しました.",
                technicalDetails: $"frameCount={_frameBuffer.FrameCount}; checksum=0x{_frameBuffer.LastFrameChecksum:X}");
            _ = ReportAsync(RendererEventType.RtspFrameCopiedToBackBuffer,
                userMessage: "デコードフレームをRendererのCPUバックバッファへコピーしました.",
                technicalDetails: $"backBufferCopyCount={_frameBuffer.FrameCount}");
        }
    }

    private string GetProgressTrace()
    {
        lock (_progressTraceLock)
        {
            return string.Join(",", _progressTrace);
        }
    }

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        lock (_libVlcTraceLock)
        {
            if (_libVlcTrace.Count >= 160)
            {
                return;
            }

            var message = RtspCredentialPattern.Replace(e.Message.Replace('\r', ' ').Replace('\n', ' '), "${scheme}[credentials-redacted]@");
            _libVlcTrace.Add($"{e.Level}/{e.Module}: {message}");
        }
    }

    private string GetLibVlcTrace()
    {
        lock (_libVlcTraceLock)
        {
            return string.Join(" || ", _libVlcTrace);
        }
    }

    private MonitorInfo FindMonitor(string monitorId)
    {
        var monitors = new DesktopMonitorProvider().GetMonitors();
        return monitors.FirstOrDefault(x => x.PersistentId.Equals(monitorId, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault()
               ?? throw new InvalidOperationException("対象ディスプレイが見つかりません。");
    }

    private RendererMetrics? BuildMetrics(DesktopHostDiscoveryResult? discovery)
    {
        var effectiveDiscovery = discovery ?? _lastDiscovery;
        if (effectiveDiscovery is null)
        {
            return null;
        }

        var health = GetPlaybackHealth();
        var parent = DesktopWindowDiagnostics.GetParent(_window.Hwnd);
        var rect = DesktopWindowDiagnostics.TryGetScreenRect(_window.Hwnd, out var screenRect)
            ? screenRect
            : new RectD();
        var style = NativeWindowDiagnostics.GetStyle(_window.Hwnd);
        var extendedStyle = NativeWindowDiagnostics.GetExtendedStyle(_window.Hwnd);
        var root = NativeWindowDiagnostics.GetRoot(_window.Hwnd);
        var owner = NativeWindowDiagnostics.GetOwner(_window.Hwnd);
        var windowClass = NativeWindowDiagnostics.GetClassName(_window.Hwnd);
        var expectedParent = effectiveDiscovery.Strategy == DesktopLayoutStrategy.RaisedDesktop
            ? nint.Zero
            : effectiveDiscovery.HostHwnd;
        return new RendererMetrics(Environment.ProcessId, _window.Hwnd, parent, expectedParent,
            health.VoutCount, _testPatternRunning ? "PlayingTestPattern" : _player?.State.ToString() ?? "Stopped",
            _testPatternRunning ? "TEST_PATTERN" : null, null, null,
            _reconnectCount, effectiveDiscovery.Strategy, rect, _lastMonitor?.Bounds ?? new RectD(), health.MediaTimeMs,
            health.LastProgressAt, health.ProgressAge == TimeSpan.MaxValue ? null : health.ProgressAge.TotalSeconds,
            NativeWindowDiagnostics.IsVisible(_window.Hwnd), windowClass, style, extendedStyle, root.ToInt64(), owner.ToInt64(),
            _window.GetPresentationMetrics(), _lastShellComposition);
    }

    private Task ReportAsync(RendererEventType type, string? errorCode = null, string? userMessage = null,
        string? technicalDetails = null, RendererMetrics? metrics = null) =>
        _report(new RendererEvent(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), type, DateTimeOffset.UtcNow,
            errorCode, userMessage, technicalDetails, metrics));

}
