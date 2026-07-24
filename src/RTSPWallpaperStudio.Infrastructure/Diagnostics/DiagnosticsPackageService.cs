using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Infrastructure.Paths;

namespace RTSPWallpaperStudio.Infrastructure.Diagnostics;

public sealed class DiagnosticsPackageService
{
    private static readonly Regex RtspCredentialPattern = new("(?<scheme>rtsp://)(?<credentials>[^@\\s]+)@", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPathService _paths;

    public DiagnosticsPackageService(AppPathService paths) => _paths = paths;

    public async Task<string> CreateAsync(RendererMetrics? metrics, string reason, CancellationToken cancellationToken = default)
    {
        var outputDirectory = Path.Combine(_paths.Root, "Diagnostics");
        Directory.CreateDirectory(outputDirectory);
        var staging = Path.Combine(outputDirectory, $"staging-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(outputDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Directory.CreateDirectory(staging);
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "logs"));
            foreach (var log in Directory.EnumerateFiles(_paths.Logs, "*.log", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var contents = await File.ReadAllTextAsync(log, cancellationToken);
                contents = RtspCredentialPattern.Replace(contents, "${scheme}[credentials-redacted]@");
                contents = Regex.Replace(contents, "(?i)(password|protectedPassword)\\s*[=:]\\s*[^,;\\r\\n}]+", "$1=[redacted]");
                await File.WriteAllTextAsync(Path.Combine(staging, "logs", Path.GetFileName(log)), contents, cancellationToken);
            }

            var settings = File.Exists(_paths.SettingsFile)
                ? await File.ReadAllTextAsync(_paths.SettingsFile, cancellationToken)
                : "{}";
            settings = Regex.Replace(settings, "(?i)(\\\"(?:password|protectedPassword)\\\"\\s*:\\s*)\\\"[^\\\"]*\\\"", "$1\"[redacted]\"");
            await File.WriteAllTextAsync(Path.Combine(staging, "settings-sanitized.json"), settings, cancellationToken);
            if (File.Exists(_paths.RuntimeStateFile))
            {
                File.Copy(_paths.RuntimeStateFile, Path.Combine(staging, "runtime-state.json"), true);
            }

            var report = new
            {
                createdAt = DateTimeOffset.UtcNow,
                reason,
                computerUse = "not-used-by-request",
                screenshot = "not-captured; command-line/native diagnostics only",
                os = Environment.OSVersion.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                metrics,
                note = "認証情報とRTSP URLの埋め込み資格情報はマスク済みです。"
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "diagnostics.json"), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "README.txt"),
                "RTSP Wallpaper Studio 診断パッケージ\nComputer Useは使用していません。Win32/APIとコマンドラインの証跡です。\n", cancellationToken);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Fastest, false);
            return zipPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
            catch
            {
                // Package creation already completed; a best-effort cleanup is safe.
            }
        }
    }
}
