using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using RTSPWallpaperStudio.App.ViewModels;
using RTSPWallpaperStudio.Interop;

namespace RTSPWallpaperStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly EmergencyHotkeyService _emergencyHotkey = new();
    private readonly Forms.NotifyIcon _trayIcon = new();
    private HwndSource? _hwndSource;
    private bool _exitRequested;
    private bool _residentNoticeShown;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        Closed += OnClosed;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        ConfigureTrayIcon();
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.PasswordInput = passwordBox.Password;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_hwndSource is null)
        {
            _viewModel.SetHotkeyWarning("緊急停止キーを登録できませんでした。診断ページからRendererを停止してください。");
            return;
        }

        _hwndSource.AddHook(WindowMessageHook);
        if (!_emergencyHotkey.Register(_hwndSource.Handle))
        {
            _viewModel.SetHotkeyWarning("Ctrl + Alt + Shift + F12 が他のアプリで使用中です。診断ページからRendererを停止してください。");
        }
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_emergencyHotkey.IsEmergencyMessage(message, wParam))
        {
            _viewModel.EmergencyStopCommand.Execute(null);
            handled = true;
        }

        return 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _emergencyHotkey.Unregister();
        _hwndSource?.RemoveHook(WindowMessageHook);
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray(showNotification: true);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray(showNotification: false);
        }
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        _trayIcon.Text = "RTSP Wallpaper Studio";
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("表示", null, (_, _) => ShowFromTray());
        menu.Items.Add("緊急停止", null, (_, _) => _viewModel.EmergencyStopCommand.Execute(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void HideToTray(bool showNotification)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Hide();
        if (showNotification && !_residentNoticeShown)
        {
            _residentNoticeShown = true;
            _trayIcon.ShowBalloonTip(3000, "RTSP Wallpaper Studio", "アプリは終了せず、通知領域に常駐しています。トレイアイコンから表示または終了できます。", Forms.ToolTipIcon.Info);
        }
    }

    public void ExitApplication()
    {
        _exitRequested = true;
        System.Windows.Application.Current.Shutdown();
    }

    public void AllowCloseForSystemShutdown() => _exitRequested = true;

    private void ViewModelOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ThemeMode))
        {
            ApplyTheme(_viewModel.ThemeMode);
        }
    }

    private static void ApplyTheme(string mode)
    {
        var highContrast = SystemParameters.HighContrast;
        var dark = string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase) ||
                   (string.Equals(mode, "System", StringComparison.OrdinalIgnoreCase) && highContrast);
        var values = highContrast
            ? new Dictionary<string, string>
            {
                ["WindowBackground"] = "#000000", ["CardBackground"] = "#000000", ["Ink"] = "#FFFFFF", ["MutedInk"] = "#FFFFFF",
                ["Accent"] = "#FFFF00", ["SidebarBackground"] = "#000000", ["SidebarForeground"] = "#FFFFFF", ["SidebarMutedForeground"] = "#FFFFFF",
                ["NavigationHoverBackground"] = "#333333", ["NavigationSelectedBackground"] = "#FFFF00", ["NavigationSelectedForeground"] = "#000000",
                ["NavigationFocusBorder"] = "#FFFFFF", ["ButtonBackground"] = "#FFFF00", ["ButtonForeground"] = "#000000", ["ButtonBorder"] = "#FFFFFF",
                ["DangerBackground"] = "#FF0000", ["DangerForeground"] = "#FFFFFF", ["FocusBorder"] = "#FFFFFF"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackground"] = dark ? "#101722" : "#F5F7FB", ["CardBackground"] = dark ? "#1B2738" : "#FFFFFF",
                ["Ink"] = dark ? "#F1F5FB" : "#1D2433", ["MutedInk"] = dark ? "#AAB9CD" : "#657087",
                ["Accent"] = dark ? "#5C91F5" : "#2764D8", ["SidebarBackground"] = dark ? "#0E1726" : "#22304A",
                ["SidebarForeground"] = "#F2F6FF", ["SidebarMutedForeground"] = "#B4C3D8", ["NavigationHoverBackground"] = dark ? "#263B5B" : "#2E4263",
                ["NavigationSelectedBackground"] = dark ? "#355A9B" : "#3C65A3", ["NavigationSelectedForeground"] = "#FFFFFF",
                ["NavigationFocusBorder"] = "#F6C453", ["ButtonBackground"] = dark ? "#2A3A52" : "#E8EDF5",
                ["ButtonForeground"] = dark ? "#F1F5FB" : "#172235", ["ButtonBorder"] = dark ? "#536A88" : "#B8C5D8",
                ["DangerBackground"] = dark ? "#C1495B" : "#B83E51", ["DangerForeground"] = "#FFFFFF", ["FocusBorder"] = "#F6C453"
            };

        foreach (var (key, value) in values)
        {
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        }
    }
}
