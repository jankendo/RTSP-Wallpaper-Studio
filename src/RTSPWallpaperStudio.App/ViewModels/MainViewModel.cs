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
using RTSPWallpaperStudio.Infrastructure.Security;
using RTSPWallpaperStudio.Infrastructure.Settings;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly ProtectedSecretStore _secretStore;
    private readonly ConnectionTester _connectionTester;
    private readonly DesktopMonitorProvider _monitorProvider;
    private readonly RendererProcessManager _rendererManager;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AppPathService _paths;
    private readonly ILogger<MainViewModel> _logger;
    private readonly AppStartupOptions _startupOptions;
    private AppSettings _settings = new();
    private RuntimeState _runtimeState = new();

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
    private string _themeMode = "Light";

    [ObservableProperty]
    private bool _isSafeMode;

    [ObservableProperty]
    private string _safeModeMessage = string.Empty;

    [ObservableProperty]
    private string _diagnosticsText = "まだ診断イベントはありません。";

    [ObservableProperty]
    private string _hotkeyWarning = string.Empty;

    public MainViewModel(
        JsonSettingsStore settingsStore,
        ProtectedSecretStore secretStore,
        ConnectionTester connectionTester,
        DesktopMonitorProvider monitorProvider,
        RendererProcessManager rendererManager,
        RuntimeStateStore runtimeStateStore,
        AppPathService paths,
        AppStartupOptions startupOptions,
        ILogger<MainViewModel> logger)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _connectionTester = connectionTester;
        _monitorProvider = monitorProvider;
        _rendererManager = rendererManager;
        _runtimeStateStore = runtimeStateStore;
        _paths = paths;
        _startupOptions = startupOptions;
        _logger = logger;
        _isSafeMode = startupOptions.SafeMode;

        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        EmergencyStopCommand = new AsyncRelayCommand(EmergencyStopAsync);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        ResetDesktopCommand = new AsyncRelayCommand(ResetDesktopAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        NavigateCommand = new RelayCommand<string>(page => CurrentPage = string.IsNullOrWhiteSpace(page) ? "home" : page);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        _rendererManager.RendererEventReceived += RendererManagerOnRendererEventReceived;
        _ = InitializeAsync();
    }

    public ObservableCollection<RtspProfile> Profiles { get; } = [];
    public ObservableCollection<MonitorInfo> Monitors { get; } = [];
    public IReadOnlyList<string> NavigationItems { get; } = ["home", "profiles", "display", "diagnostics", "settings"];
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand TestConnectionCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand EmergencyStopCommand { get; }
    public IAsyncRelayCommand ReconnectCommand { get; }
    public IAsyncRelayCommand ResetDesktopCommand { get; }
    public IAsyncRelayCommand SaveSettingsCommand { get; }
    public IRelayCommand<string> NavigateCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }
    public string PasswordInput { get; set; } = string.Empty;

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

    public async Task MarkCleanShutdownAsync()
    {
        _runtimeState.PreviousShutdownClean = true;
        _runtimeState.WallpaperApplyInProgress = false;
        await _runtimeStateStore.SaveAsync(_runtimeState);
    }

    private async Task InitializeAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            _runtimeState = await _runtimeStateStore.LoadAsync();
            if (!_runtimeState.PreviousShutdownClean || _runtimeState.WallpaperApplyInProgress)
            {
                IsSafeMode = true;
                SafeModeMessage = "前回の終了が不完全だったため、壁紙の自動復元を停止しています。確認後に手動で設定してください。";
            }

            _runtimeState.PreviousShutdownClean = false;
            await _runtimeStateStore.SaveAsync(_runtimeState);
            ThemeMode = string.IsNullOrWhiteSpace(_settings.ThemeMode) ? "Light" : _settings.ThemeMode;
            Profiles.Clear();
            foreach (var profile in _settings.Profiles)
            {
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault(x => x.Id == _settings.SelectedProfileId) ?? Profiles.FirstOrDefault();
            RefreshMonitors();
            SelectedMonitor = Monitors.FirstOrDefault(x => x.PersistentId == _settings.SelectedMonitorId) ?? Monitors.FirstOrDefault(x => x.IsPrimary) ?? Monitors.FirstOrDefault();
            StatusMessage = IsSafeMode
                ? "セーフモードで起動しました。既存の壁紙は自動復元していません。"
                : $"{Monitors.Count}台のディスプレイを検出しました。RTSP URLを確認して設定してください。";
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

        StatusMessage = "LibVLCでRTSP映像トラックを解析しています…";
        var result = await _connectionTester.TestAsync(SelectedProfile.Url, SelectedProfile.ConnectionTimeoutSeconds);
        StatusMessage = result.Success ? $"解析成功：{result.Elapsed.TotalMilliseconds:0}ms\n{result.Details}" : $"{result.Summary}\n{result.Details}";
        FooterMessage = result.Success
            ? "RTSP映像トラックを確認しました。壁紙設定時はRendererが最初の映像出力を確認してから表示します。"
            : "RTSP映像を確認できませんでした。壁紙を表示せず、URL・配信ソフト・ファイアウォールを確認してください。";
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
        if (!string.IsNullOrWhiteSpace(parts.UserName))
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
                Environment.ProcessId));
            SelectedProfile.LastStatus = PlaybackStatus.Starting;
            OnPropertyChanged(nameof(CurrentProfileStatus));
            StatusMessage = "Rendererへ設定を送信しました。最初の映像出力とデスクトップ配置を確認しています。";
            FooterMessage = "成功表示はWallpaperVisibleイベント受信後だけに更新されます。";
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
        await _settingsStore.SaveAsync(_settings);
        StatusMessage = "設定を保存しました。";
    }

    private void OpenLogs()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.Logs}\"") { UseShellExecute = true });
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
        DiagnosticsText = $"{rendererEvent.Timestamp:HH:mm:ss}  {rendererEvent.Type}\n" +
                          $"コード：{rendererEvent.ErrorCode ?? "なし"}\n" +
                          $"内容：{rendererEvent.UserMessage ?? "-"}\n" +
                          $"技術情報：{rendererEvent.TechnicalDetails ?? "-"}";
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
            case RendererEventType.WallpaperVisible:
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

                StatusMessage = "壁紙を表示しました。映像出力・親ウィンドウ・矩形検証を通過しています。";
                FooterMessage = "再生中です。Ctrl + Alt + Shift + F12 でいつでも停止できます。";
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
