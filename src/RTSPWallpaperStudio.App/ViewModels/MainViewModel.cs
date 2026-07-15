using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.App;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;
using RTSPWallpaperStudio.Infrastructure.Diagnostics;
using RTSPWallpaperStudio.Infrastructure.Ipc;
using RTSPWallpaperStudio.Infrastructure.Paths;
using RTSPWallpaperStudio.Infrastructure.Relay;
using RTSPWallpaperStudio.Infrastructure.Security;
using RTSPWallpaperStudio.Infrastructure.Settings;
using RTSPWallpaperStudio.Infrastructure.Startup;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly ProtectedSecretStore _secretStore;
    private readonly ConnectionTester _connectionTester;
    private readonly DiagnosticsPackageService _diagnosticsPackageService;
    private readonly DesktopMonitorProvider _monitorProvider;
    private readonly RendererProcessManager _rendererManager;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AppPathService _paths;
    private readonly ILogger<MainViewModel> _logger;
    private readonly AppStartupOptions _startupOptions;
    private readonly Go2RtcProcessManager _go2RtcProcessManager;
    private readonly StartupRegistrationService _startupRegistrationService;
    private AppSettings _settings = new();
    private RuntimeState _runtimeState = new();
    private CancellationTokenSource? _connectionTestCts;
    private RendererMetrics? _lastRendererMetrics;

    [ObservableProperty]
    private RtspProfile? _selectedProfile;

    [ObservableProperty]
    private MonitorInfo? _selectedMonitor;

    [ObservableProperty]
    private string _statusMessage = "設定を読み込んでいます。";

    [ObservableProperty]
    private string _footerMessage = "Ctrl + Alt + Shift + F12 で再生を即時停止できます。";

    [ObservableProperty]
    private string _currentPage = "home";

    [ObservableProperty]
    private string _themeMode = "Dark";

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _startMinimized = true;

    [ObservableProperty]
    private string _startupRegistrationStatus = "Windows起動時設定を確認しています。";

    [ObservableProperty]
    private bool _isSafeMode;

    [ObservableProperty]
    private string _safeModeMessage = string.Empty;

    [ObservableProperty]
    private string _diagnosticsText = "まだ診断イベントはありません。";

    [ObservableProperty]
    private string _hotkeyWarning = string.Empty;

    [ObservableProperty]
    private NavigationItemViewModel? _selectedNavigationItem;

    [ObservableProperty]
    private bool _isConnectionTestRunning;

    [ObservableProperty]
    private double _connectionTestProgress;

    [ObservableProperty]
    private string _connectionTestStage = "未実行";

    [ObservableProperty]
    private string _connectionTestErrorCode = string.Empty;

    [ObservableProperty]
    private string _connectionTestTechnicalDetails = string.Empty;

    [ObservableProperty]
    private bool _canApplyWallpaper;

    [ObservableProperty]
    private bool _canCancelConnectionTest;

    [ObservableProperty]
    private string _relayStatus = "go2rtcを確認しています。";

    [ObservableProperty]
    private string _playbackHealth = "映像ヘルスを待機しています。";

    public MainViewModel(
        JsonSettingsStore settingsStore,
        ProtectedSecretStore secretStore,
        ConnectionTester connectionTester,
        DiagnosticsPackageService diagnosticsPackageService,
        DesktopMonitorProvider monitorProvider,
        RendererProcessManager rendererManager,
        RuntimeStateStore runtimeStateStore,
        AppPathService paths,
        AppStartupOptions startupOptions,
        Go2RtcProcessManager go2RtcProcessManager,
        StartupRegistrationService startupRegistrationService,
        ILogger<MainViewModel> logger)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _connectionTester = connectionTester;
        _diagnosticsPackageService = diagnosticsPackageService;
        _monitorProvider = monitorProvider;
        _rendererManager = rendererManager;
        _runtimeStateStore = runtimeStateStore;
        _paths = paths;
        _startupOptions = startupOptions;
        _go2RtcProcessManager = go2RtcProcessManager;
        _startupRegistrationService = startupRegistrationService;
        _logger = logger;
        _isSafeMode = startupOptions.SafeMode;

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApplyWallpaper && !IsConnectionTestRunning);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsConnectionTestRunning);
        CancelConnectionTestCommand = new AsyncRelayCommand(CancelConnectionTestAsync, () => CanCancelConnectionTest);
        StopCommand = new AsyncRelayCommand(StopAsync);
        EmergencyStopCommand = new AsyncRelayCommand(EmergencyStopAsync);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        ResetDesktopCommand = new AsyncRelayCommand(ResetDesktopAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        WallpaperSelfTestCommand = new AsyncRelayCommand(RunWallpaperSelfTestAsync);
        CreateDiagnosticsPackageCommand = new AsyncRelayCommand(CreateDiagnosticsPackageAsync);
        NavigateCommand = new RelayCommand<string>(page => NavigateTo(page));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        _rendererManager.RendererEventReceived += RendererManagerOnRendererEventReceived;
        InitializationTask = InitializeAsync();
    }

    public ObservableCollection<RtspProfile> Profiles { get; } = [];
    public ObservableCollection<MonitorInfo> Monitors { get; } = [];
    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; } =
    [
        new("home", "ライブラリ", "⌂"),
        new("profiles", "ストリーム", "◈"),
        new("display", "ディスプレイ", "▣"),
        new("diagnostics", "パフォーマンス", "◌"),
        new("settings", "設定", "⚙")
    ];
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand TestConnectionCommand { get; }
    public IAsyncRelayCommand CancelConnectionTestCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand EmergencyStopCommand { get; }
    public IAsyncRelayCommand ReconnectCommand { get; }
    public IAsyncRelayCommand ResetDesktopCommand { get; }
    public IAsyncRelayCommand SaveSettingsCommand { get; }
    public IAsyncRelayCommand WallpaperSelfTestCommand { get; }
    public IAsyncRelayCommand CreateDiagnosticsPackageCommand { get; }
    public IRelayCommand<string> NavigateCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }
    public Task InitializationTask { get; }
    public string PasswordInput { get; set; } = string.Empty;
    public string UsernameInput { get; set; } = string.Empty;

    public string CurrentProfileStatus => SelectedProfile?.LastStatus switch
    {
        PlaybackStatus.Playing => "再生中",
        PlaybackStatus.Starting or PlaybackStatus.Connecting => "接続中",
        PlaybackStatus.Buffering => "バッファリング中",
        PlaybackStatus.Reconnecting => "再接続中",
        PlaybackStatus.Failed => "エラー",
        _ => "停止中"
    };

    public void SetHotkeyWarning(string warning) => HotkeyWarning = warning;

    partial void OnSelectedNavigationItemChanged(NavigationItemViewModel? value)
    {
        if (value is not null)
        {
            CurrentPage = value.Id;
        }
    }

    private void NavigateTo(string? page)
    {
        _connectionTestCts?.Cancel();
        var target = string.IsNullOrWhiteSpace(page) ? "home" : page;
        SelectedNavigationItem = NavigationItems.FirstOrDefault(x => x.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
        CurrentPage = SelectedNavigationItem?.Id ?? "home";
    }

    public async Task MarkCleanShutdownAsync()
    {
        _connectionTestCts?.Cancel();
        _runtimeState.PreviousShutdownClean = true;
        _runtimeState.WallpaperApplyInProgress = false;
        await _runtimeStateStore.SaveAsync(_runtimeState);
    }

    private async Task InitializeAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            var relay = await _go2RtcProcessManager.EnsureStartedAsync();
            RelayStatus = relay.Status;
            _runtimeState = await _runtimeStateStore.LoadAsync();
            if (!_runtimeState.PreviousShutdownClean || _runtimeState.WallpaperApplyInProgress)
            {
                IsSafeMode = true;
                SafeModeMessage = "前回の終了が不完全だったため、壁紙の自動復元を停止しています。確認後に手動で設定してください。";
            }

            _runtimeState.PreviousShutdownClean = false;
            await _runtimeStateStore.SaveAsync(_runtimeState);
            ThemeMode = string.IsNullOrWhiteSpace(_settings.ThemeMode) ? "Dark" : _settings.ThemeMode;
            StartWithWindows = _settings.StartWithWindows;
            StartMinimized = _settings.StartMinimized;
            SynchronizeStartupRegistration();
            Profiles.Clear();
            foreach (var profile in _settings.Profiles)
            {
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault(x => x.Id == _settings.SelectedProfileId) ?? Profiles.FirstOrDefault();
            SelectedNavigationItem = NavigationItems[0];
            UsernameInput = SelectedProfile?.UserName ?? string.Empty;
            RefreshMonitors();
            SelectedMonitor = Monitors.FirstOrDefault(x => x.PersistentId == _settings.SelectedMonitorId) ?? Monitors.FirstOrDefault(x => x.IsPrimary) ?? Monitors.FirstOrDefault();
            StatusMessage = IsSafeMode
                ? "セーフモードで起動しました。既存の壁紙は自動復元していません。"
                : $"{Monitors.Count}台のディスプレイを検出しました。RTSP URLを確認して設定してください。";
            if (!relay.Available)
            {
                StatusMessage += $"\n{relay.Status}";
            }
            if (_startupOptions.StopAll)
            {
                await EmergencyStopAsync();
            }

            if (_startupOptions.ResetDesktop)
            {
                await ResetDesktopAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初期化に失敗しました。");
            StatusMessage = "設定の読み込みに失敗しました。初期値で続行します。";
        }
    }

    private void RefreshMonitors()
    {
        Monitors.Clear();
        foreach (var monitor in _monitorProvider.GetMonitors())
        {
            Monitors.Add(monitor);
        }
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedProfile is null)
        {
            StatusMessage = "プロファイルを選択してください。";
            return;
        }

        if (!RtspUrlService.TryNormalize(SelectedProfile.Url, out var parts, out var error))
        {
            ConnectionTestErrorCode = RendererErrorCodes.RtspOpenFailed;
            ConnectionTestTechnicalDetails = error;
            StatusMessage = error;
            CanApplyWallpaper = false;
            return;
        }

        IsConnectionTestRunning = true;
        CanCancelConnectionTest = true;
        CanApplyWallpaper = false;
        ConnectionTestProgress = 0;
        ConnectionTestStage = "ValidatingUrl";
        ConnectionTestErrorCode = string.Empty;
        ConnectionTestTechnicalDetails = string.Empty;
        ConnectionTestSteps.Clear();
        _connectionTestCts?.Dispose();
        _connectionTestCts = new CancellationTokenSource();
        var userName = !string.IsNullOrWhiteSpace(UsernameInput)
            ? UsernameInput
            : !string.IsNullOrWhiteSpace(parts.UserName) ? parts.UserName : SelectedProfile.UserName;
        var password = !string.IsNullOrEmpty(PasswordInput)
            ? PasswordInput
            : parts.Password ?? _secretStore.Unprotect(SelectedProfile.ProtectedPassword);
        var request = new ConnectionTestRequest(parts.Url, userName, password, SelectedProfile.Transport,
            SelectedProfile.NetworkCachingMs, SelectedProfile.ConnectionTimeoutSeconds,
            SelectedProfile.HardwareDecode, SelectedProfile.MuteAudio);
        StatusMessage = "LibVLCで再生と映像出力を確認しています…";
        try
        {
            var progress = new Progress<ConnectionTestProgress>(UpdateConnectionTestProgress);
            var result = await _connectionTester.TestAsync(request, progress, _connectionTestCts.Token);
            ConnectionTestErrorCode = result.ErrorCode ?? string.Empty;
            ConnectionTestTechnicalDetails = result.TechnicalDetails ?? result.Details;
            foreach (var step in result.Steps ?? [])
            {
                if (!ConnectionTestSteps.Contains(step)) ConnectionTestSteps.Add(step);
            }

            CanApplyWallpaper = result.Success;
            StatusMessage = result.Success
                ? $"接続成功：{result.Elapsed.TotalMilliseconds:0}ms\n{result.Details}"
                : $"{result.Summary}\n{result.Details}";
            FooterMessage = result.Success
                ? $"接続方式：{result.ActualTransport ?? "自動"}。PlayingかつVoutCount>0を確認しました。壁紙設定へ進めます。"
                : "接続テスト失敗。壁紙は表示しません。診断とログのエラーコードを確認してください。";
        }
        catch (OperationCanceledException)
        {
            ConnectionTestStage = "Cancelled";
            ConnectionTestErrorCode = "RTSP_TEST_CANCELLED";
            ConnectionTestTechnicalDetails = "接続テストをキャンセルしました。";
            StatusMessage = "接続テストをキャンセルしました。";
            FooterMessage = "壁紙は表示していません。必要ならURLを確認して再試行してください。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "接続テストに失敗しました。");
            ConnectionTestStage = "Failed";
            ConnectionTestErrorCode = RendererErrorCodes.RtspOpenFailed;
            ConnectionTestTechnicalDetails = ex.ToString();
            StatusMessage = "接続テストに失敗しました。";
            FooterMessage = "診断ページとログを確認してください。パスワードはログに記録しません。";
        }
        finally
        {
            IsConnectionTestRunning = false;
            CanCancelConnectionTest = false;
            _connectionTestCts?.Dispose();
            _connectionTestCts = null;
            TestConnectionCommand.NotifyCanExecuteChanged();
            CancelConnectionTestCommand.NotifyCanExecuteChanged();
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    public ObservableCollection<string> ConnectionTestSteps { get; } = [];

    private void UpdateConnectionTestProgress(ConnectionTestProgress update)
    {
        ConnectionTestStage = update.Stage.ToString();
        ConnectionTestProgress = update.Percent;
        foreach (var step in update.Steps)
        {
            if (!ConnectionTestSteps.Contains(step)) ConnectionTestSteps.Add(step);
        }

        StatusMessage = update.Message;
    }

    private Task CancelConnectionTestAsync()
    {
        _connectionTestCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task ApplyAsync()
    {
        if (SelectedProfile is null || SelectedMonitor is null)
        {
            StatusMessage = "プロファイルと対象ディスプレイを選択してください。";
            return;
        }

        if (!RtspUrlService.TryNormalize(SelectedProfile.Url, out var parts, out var error))
        {
            StatusMessage = error;
            return;
        }

        SelectedProfile.Url = parts.Url;
        if (!string.IsNullOrWhiteSpace(UsernameInput))
        {
            SelectedProfile.UserName = UsernameInput;
        }
        else if (!string.IsNullOrWhiteSpace(parts.UserName))
        {
            SelectedProfile.UserName = parts.UserName;
        }

        if (!string.IsNullOrWhiteSpace(PasswordInput))
        {
            SelectedProfile.ProtectedPassword = _secretStore.Protect(PasswordInput);
        }

        _settings.SelectedProfileId = SelectedProfile.Id;
        _settings.SelectedMonitorId = SelectedMonitor.PersistentId;
        _settings.Profiles = Profiles.ToList();
        await _settingsStore.SaveAsync(_settings);
        _runtimeState.WallpaperApplyInProgress = true;
        _runtimeState.LastFailureCode = null;
        await _runtimeStateStore.SaveAsync(_runtimeState);

        try
        {
            await _rendererManager.StopAllAsync();
            await _rendererManager.StartAsync(new RendererStartOptions(
                parts.Url,
                SelectedProfile.UserName,
                _secretStore.Unprotect(SelectedProfile.ProtectedPassword),
                SelectedProfile.Transport,
                SelectedProfile.NetworkCachingMs,
                SelectedProfile.DisplayMode,
                SelectedMonitor.PersistentId,
                Environment.ProcessId,
                SelectedProfile.HardwareDecode,
                SelectedProfile.MuteAudio));
            SelectedProfile.LastStatus = PlaybackStatus.Starting;
            OnPropertyChanged(nameof(CurrentProfileStatus));
            StatusMessage = "Rendererへ設定を送信しました。最初の映像出力とデスクトップ配置を確認しています。";
            FooterMessage = "GDI描画・実デスクトップ画素・継続表示の検証完了後だけ成功になります。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "壁紙設定に失敗しました。");
            await MarkFailureAsync(RendererErrorCodes.RendererCrashLoop, "Rendererを起動できませんでした。Portable配置とログを確認してください。", ex.Message);
        }
    }

    private async Task StopAsync()
    {
        await _rendererManager.StopAllAsync();
        _runtimeState.WallpaperApplyInProgress = false;
        await _runtimeStateStore.SaveAsync(_runtimeState);
        if (SelectedProfile is not null)
        {
            SelectedProfile.LastStatus = PlaybackStatus.Stopped;
            OnPropertyChanged(nameof(CurrentProfileStatus));
        }

        StatusMessage = "壁紙を停止しました。";
    }

    private async Task RunWallpaperSelfTestAsync()
    {
        var monitor = SelectedMonitor ?? Monitors.FirstOrDefault();
        if (monitor is null)
        {
            StatusMessage = "セルフテスト対象のディスプレイが見つかりません。";
            return;
        }

        try
        {
            await _rendererManager.StopAllAsync();
            await _rendererManager.StartAsync(new RendererStartOptions(
                "rtsp://127.0.0.1:8554/self-test",
                null,
                null,
                TransportMode.Tcp,
                300,
                DisplayMode.Fill,
                monitor.PersistentId,
                Environment.ProcessId,
                HardwareDecodeMode.Disabled,
                true,
                true));
            StatusMessage = "壁紙描画セルフテストを実行中です。HWND、Raised Desktop、GDI、実画素を検証します。";
            FooterMessage = "成功表示はWallpaperEndToEndVerifiedイベント受信後だけに更新されます。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "壁紙描画セルフテストの起動に失敗しました。");
            StatusMessage = "壁紙描画セルフテストを起動できませんでした。";
            FooterMessage = ex.Message;
        }
    }

    private async Task CreateDiagnosticsPackageAsync()
    {
        try
        {
            var path = await _diagnosticsPackageService.CreateAsync(_lastRendererMetrics, "GUI diagnostics package command");
            StatusMessage = "診断パッケージを作成しました。";
            FooterMessage = path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "診断パッケージの作成に失敗しました。");
            StatusMessage = "診断パッケージの作成に失敗しました。";
            FooterMessage = ex.Message;
        }
    }

    private async Task EmergencyStopAsync()
    {
        await _rendererManager.StopAllAsync();
        _runtimeState.WallpaperApplyInProgress = false;
        _runtimeState.LastFailureCode = null;
        await _runtimeStateStore.SaveAsync(_runtimeState);
        if (SelectedProfile is not null)
        {
            SelectedProfile.LastStatus = PlaybackStatus.Stopped;
            OnPropertyChanged(nameof(CurrentProfileStatus));
        }

        StatusMessage = "緊急停止を実行しました。Rendererと子プロセスを停止しています。";
        FooterMessage = "デスクトップ保護のため、画面が黒い場合は再起動せず診断を確認してください。";
    }

    private async Task ReconnectAsync()
    {
        await _rendererManager.SendCommandAsync("reconnect");
        StatusMessage = "Rendererへ再接続を要求しました。";
    }

    public async Task ResetDesktopAsync()
    {
        await _rendererManager.SendCommandAsync("reset-shell");
        StatusMessage = "デスクトップホストの再探索を要求しました。Explorer自体は再起動していません。";
    }

    private async Task SaveSettingsAsync()
    {
        _settings.Profiles = Profiles.ToList();
        _settings.SelectedProfileId = SelectedProfile?.Id;
        _settings.SelectedMonitorId = SelectedMonitor?.PersistentId;
        _settings.ThemeMode = ThemeMode;
        _settings.StartWithWindows = StartWithWindows;
        _settings.StartMinimized = StartMinimized;
        var startupResult = _startupRegistrationService.SetEnabled(StartWithWindows);
        StartupRegistrationStatus = startupResult.Message;
        if (!startupResult.Success)
        {
            StatusMessage = $"設定を保存できませんでした。{startupResult.Message}\nエラーコード：{startupResult.ErrorCode}";
            FooterMessage = startupResult.TechnicalDetails ?? "診断ページとログを確認してください。";
            return;
        }

        await _settingsStore.SaveAsync(_settings);
        StatusMessage = StartWithWindows
            ? "設定を保存しました。Windows起動時にトレイへ常駐します。"
            : "設定を保存しました。Windows起動時の自動起動は無効です。";
    }

    private void OpenLogs()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.Logs}\"") { UseShellExecute = true });
    }

    private void SynchronizeStartupRegistration()
    {
        var current = _startupRegistrationService.GetStatus();
        if (!current.Success)
        {
            StartupRegistrationStatus = current.Message;
            return;
        }

        if (current.IsEnabled != StartWithWindows)
        {
            var result = _startupRegistrationService.SetEnabled(StartWithWindows);
            StartupRegistrationStatus = result.Message;
            if (!result.Success)
            {
                _logger.LogWarning("Windows起動時設定の同期に失敗しました。code={ErrorCode} details={Details}", result.ErrorCode, result.TechnicalDetails);
            }

            return;
        }

        StartupRegistrationStatus = current.Message;
    }

    private void RendererManagerOnRendererEventReceived(object? sender, RendererEvent rendererEvent)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplyRendererEvent(rendererEvent);
            return;
        }

        _ = dispatcher.InvokeAsync(() => ApplyRendererEvent(rendererEvent));
    }

    private void ApplyRendererEvent(RendererEvent rendererEvent)
    {
        if (rendererEvent.Metrics is { } eventMetrics)
        {
            _lastRendererMetrics = eventMetrics;
        }
        UpdatePlaybackHealth(rendererEvent);
        if (rendererEvent.Type != RendererEventType.Heartbeat)
        {
            DiagnosticsText = $"{rendererEvent.Timestamp:HH:mm:ss}  {rendererEvent.Type}\n" +
                              $"コード：{rendererEvent.ErrorCode ?? "なし"}\n" +
                              $"内容：{rendererEvent.UserMessage ?? "-"}\n" +
                              $"技術情報：{rendererEvent.TechnicalDetails ?? "-"}" +
                              (rendererEvent.Metrics is null ? string.Empty : $"\n\n映像ヘルス：{PlaybackHealth}");
        }
        switch (rendererEvent.Type)
        {
            case RendererEventType.RendererReady:
                StatusMessage = "Rendererに接続しました。";
                break;
            case RendererEventType.StreamOpening:
                if (SelectedProfile is not null) SelectedProfile.LastStatus = PlaybackStatus.Connecting;
                StatusMessage = "RTSPストリームを開いています。";
                break;
            case RendererEventType.Buffering:
                if (SelectedProfile is not null) SelectedProfile.LastStatus = PlaybackStatus.Buffering;
                StatusMessage = "映像をバッファリングしています。";
                break;
            case RendererEventType.PlaybackStalled:
                if (SelectedProfile is not null) SelectedProfile.LastStatus = PlaybackStatus.Reconnecting;
                StatusMessage = "映像の進行停止を検出しました。自動再接続しています。";
                FooterMessage = "フリーズ監視がMediaPlayerを安全に再生成しています。壁紙は復旧完了まで表示しません。";
                break;
            case RendererEventType.Reconnecting:
                if (SelectedProfile is not null) SelectedProfile.LastStatus = PlaybackStatus.Reconnecting;
                StatusMessage = "RTSPストリームを再接続しています。";
                break;
            case RendererEventType.WallpaperEndToEndVerified:
                if (SelectedProfile is not null)
                {
                    SelectedProfile.LastStatus = PlaybackStatus.Playing;
                    SelectedProfile.LastConnectedAt = DateTimeOffset.UtcNow;
                    _runtimeState.LastSuccessfulProfileId = SelectedProfile.Id;
                    _runtimeState.LastSuccessfulMonitorId = SelectedMonitor?.PersistentId;
                    _runtimeState.LastRendererPid = rendererEvent.Metrics?.ProcessId;
                    _runtimeState.WallpaperApplyInProgress = false;
                    _ = _runtimeStateStore.SaveAsync(_runtimeState);
                    OnPropertyChanged(nameof(CurrentProfileStatus));
                }

                StatusMessage = "壁紙を表示しました。GDI描画・実デスクトップ画素・継続表示まで検証済みです。";
                FooterMessage = "再生中です。Ctrl + Alt + Shift + F12 でいつでも停止できます。";
                break;
            case RendererEventType.WallpaperVisible:
                StatusMessage = "Renderer HWNDは可視ですが、最終成功判定を継続検証しています。";
                break;
            case RendererEventType.PlaybackRunning:
                StatusMessage = "再生中です。";
                break;
            case RendererEventType.FatalError:
            case RendererEventType.AttachmentFailed:
            case RendererEventType.PlaybackError:
                _ = MarkFailureAsync(rendererEvent.ErrorCode ?? RendererErrorCodes.RtspOpenFailed,
                    rendererEvent.UserMessage ?? "Rendererでエラーが発生しました。", rendererEvent.TechnicalDetails);
                break;
            case RendererEventType.Stopped:
                StatusMessage = "Rendererを停止しました。";
                break;
        }

        OnPropertyChanged(nameof(CurrentProfileStatus));
    }

    private void UpdatePlaybackHealth(RendererEvent rendererEvent)
    {
        if (rendererEvent.Metrics is not { } metrics)
        {
            return;
        }

        var age = metrics.VideoProgressAgeSeconds is { } seconds
            ? $"{seconds:0.0}秒前"
            : "未取得";
        PlaybackHealth = $"{metrics.MediaState}  ·  Vout {metrics.VoutCount}  ·  映像進行 {age}  ·  MediaTime {metrics.MediaTimeMs}ms  ·  再接続 {metrics.ReconnectCount}回\n" +
                         $"HWND 0x{metrics.RendererHwnd.ToInt64():X}  ·  親 0x{metrics.ParentHwnd.ToInt64():X} / 期待値 0x{metrics.ExpectedParentHwnd.ToInt64():X}  ·  表示 {metrics.WindowVisible}  ·  矩形 {metrics.RendererRect}  ·  モニター {metrics.MonitorRect}";
    }

    private async Task MarkFailureAsync(string code, string message, string? technicalDetails)
    {
        _runtimeState.WallpaperApplyInProgress = false;
        _runtimeState.LastFailureCode = code;
        _runtimeState.LastFailureAt = DateTimeOffset.UtcNow;
        await _runtimeStateStore.SaveAsync(_runtimeState);
        if (SelectedProfile is not null)
        {
            SelectedProfile.LastStatus = PlaybackStatus.Failed;
            SelectedProfile.LastError = $"{code}: {message}";
        }

        StatusMessage = message;
        FooterMessage = $"エラーコード：{code}\n{technicalDetails ?? "診断ページとログを確認してください。"}";
        OnPropertyChanged(nameof(CurrentProfileStatus));
    }
}
