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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
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
        menu.Items.Add("終了", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ViewModelOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ThemeMode))
        {
            ApplyTheme(_viewModel.ThemeMode);
        }
    }

    private static void ApplyTheme(string mode)
    {
        var dark = string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase);
        System.Windows.Application.Current.Resources["WindowBackground"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#101722" : "#F5F7FB"));
        System.Windows.Application.Current.Resources["CardBackground"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#1B2738" : "#FFFFFF"));
        System.Windows.Application.Current.Resources["Ink"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#F1F5FB" : "#1D2433"));
        System.Windows.Application.Current.Resources["MutedInk"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#AAB9CD" : "#657087"));
        System.Windows.Application.Current.Resources["Accent"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#5C91F5" : "#2764D8"));
    }
}
