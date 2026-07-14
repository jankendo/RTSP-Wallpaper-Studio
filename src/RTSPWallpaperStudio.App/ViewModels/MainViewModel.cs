using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;
using RTSPWallpaperStudio.Infrastructure.Diagnostics;
using RTSPWallpaperStudio.Infrastructure.Ipc;
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
    private readonly ILogger<MainViewModel> _logger;
    private AppSettings _settings = new();

    [ObservableProperty]
    private RtspProfile? _selectedProfile;

    [ObservableProperty]
    private MonitorInfo? _selectedMonitor;

    [ObservableProperty]
    private string _statusMessage = "設定を読み込んでいます。";

    [ObservableProperty]
    private string _footerMessage = "WorkerWへの配置に失敗した場合は、診断メッセージを確認してください。";

    public MainViewModel(
        JsonSettingsStore settingsStore,
        ProtectedSecretStore secretStore,
        ConnectionTester connectionTester,
        DesktopMonitorProvider monitorProvider,
        RendererProcessManager rendererManager,
        ILogger<MainViewModel> logger)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _connectionTester = connectionTester;
        _monitorProvider = monitorProvider;
        _rendererManager = rendererManager;
        _logger = logger;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        _ = InitializeAsync();
    }

    public ObservableCollection<RtspProfile> Profiles { get; } = [];
    public ObservableCollection<MonitorInfo> Monitors { get; } = [];
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand TestConnectionCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public string PasswordInput { get; set; } = string.Empty;

    private async Task InitializeAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            Profiles.Clear();
            foreach (var profile in _settings.Profiles)
            {
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault(x => x.Id == _settings.SelectedProfileId) ?? Profiles.FirstOrDefault();
            RefreshMonitors();
            SelectedMonitor = Monitors.FirstOrDefault(x => x.PersistentId == _settings.SelectedMonitorId) ?? Monitors.FirstOrDefault(x => x.IsPrimary) ?? Monitors.FirstOrDefault();
            StatusMessage = $"{Monitors.Count}台のディスプレイを検出しました。RTSP URLを確認して設定してください。";
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

        StatusMessage = "接続を確認しています…";
        var result = await _connectionTester.TestAsync(SelectedProfile.Url, SelectedProfile.ConnectionTimeoutSeconds);
        StatusMessage = result.Success ? $"接続成功：{result.Elapsed.TotalMilliseconds:0}ms\n{result.Details}" : $"{result.Summary}\n{result.Details}";
        FooterMessage = result.Success ? "TCP接続まで確認しました。RTSP映像の再生はRendererで行います。" : "接続できない場合はURL、配信ソフト、ファイアウォールを確認してください。";
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
            SelectedProfile.LastConnectedAt = DateTimeOffset.UtcNow;
            StatusMessage = $"{SelectedMonitor.FriendlyName}へ壁紙を設定しました。Rendererの接続を待っています。";
            FooterMessage = "再生中にExplorerが再起動した場合は、RendererがWorkerWを再探索します。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "壁紙設定に失敗しました。");
            SelectedProfile.LastStatus = PlaybackStatus.Failed;
            StatusMessage = "Rendererを起動できませんでした。Portable配置とログを確認してください。";
        }
    }

    private async Task StopAsync()
    {
        await _rendererManager.StopAllAsync();
        if (SelectedProfile is not null)
        {
            SelectedProfile.LastStatus = PlaybackStatus.Stopped;
        }

        StatusMessage = "壁紙を停止しました。";
    }
}
