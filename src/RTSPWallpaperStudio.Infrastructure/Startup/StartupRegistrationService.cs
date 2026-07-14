using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace RTSPWallpaperStudio.Infrastructure.Startup;

/// <summary>
/// Registers the current user's RTSP Wallpaper Studio launch command in HKCU.
/// HKCU is intentional: enabling this setting never requires administrator rights.
/// </summary>
public sealed class StartupRegistrationService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "RTSPWallpaperStudio";

    public StartupRegistrationStatus GetStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = key?.GetValue(ValueName) as string;
            var enabled = !string.IsNullOrWhiteSpace(command);
            return new StartupRegistrationStatus(
                Success: true,
                IsEnabled: enabled,
                Command: command,
                Message: enabled
                    ? "Windows起動時の自動起動は有効です。"
                    : "Windows起動時の自動起動は無効です。");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new StartupRegistrationStatus(
                Success: false,
                IsEnabled: false,
                Command: null,
                Message: "Windows起動時設定を確認できませんでした。",
                ErrorCode: "STARTUP_REGISTRY_READ_FAILED",
                TechnicalDetails: ex.Message);
        }
    }

    public StartupRegistrationResult SetEnabled(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return Failure("STARTUP_REGISTRY_OPEN_FAILED", "Windows起動時設定を変更できませんでした。レジストリキーを開けません。", false);
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return new StartupRegistrationResult(true, false, null, "Windows起動時の自動起動を無効にしました。");
            }

            var resolvedPath = ResolveExecutablePath(executablePath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                return Failure("STARTUP_EXECUTABLE_NOT_FOUND", "自動起動に登録するアプリ実行ファイルを特定できませんでした。", false);
            }

            var command = BuildLaunchCommand(resolvedPath);
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return new StartupRegistrationResult(true, true, command, "Windows起動時の自動起動を有効にしました。");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Failure("STARTUP_REGISTRY_WRITE_FAILED", "Windows起動時設定を変更できませんでした。ユーザー権限でレジストリを書き込めません。", enabled, ex.Message);
        }
    }

    public static string BuildLaunchCommand(string executablePath, string arguments = "--startup")
    {
        var normalizedPath = Path.GetFullPath(executablePath);
        return string.IsNullOrWhiteSpace(arguments)
            ? $"\"{normalizedPath}\""
            : $"\"{normalizedPath}\" {arguments}";
    }

    public static string? ResolveExecutablePath(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var candidate = Path.GetFullPath(preferredPath);
            return File.Exists(candidate) ? candidate : null;
        }

        var appHostPath = Path.Combine(AppContext.BaseDirectory, "RTSPWallpaperStudio.App.exe");
        if (File.Exists(appHostPath))
        {
            return appHostPath;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && !IsDotnetHost(processPath) && File.Exists(processPath))
        {
            return Path.GetFullPath(processPath);
        }

        try
        {
            var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(modulePath) && File.Exists(modulePath)
                ? Path.GetFullPath(modulePath)
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsDotnetHost(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static StartupRegistrationResult Failure(string code, string message, bool enabled, string? technicalDetails = null) =>
        new(false, enabled, null, message, code, technicalDetails);
}

public sealed record StartupRegistrationStatus(
    bool Success,
    bool IsEnabled,
    string? Command,
    string Message,
    string? ErrorCode = null,
    string? TechnicalDetails = null);

public sealed record StartupRegistrationResult(
    bool Success,
    bool IsEnabled,
    string? Command,
    string Message,
    string? ErrorCode = null,
    string? TechnicalDetails = null);
