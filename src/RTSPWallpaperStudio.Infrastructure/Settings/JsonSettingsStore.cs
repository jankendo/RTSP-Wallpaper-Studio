using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Infrastructure.Paths;

namespace RTSPWallpaperStudio.Infrastructure.Settings;

public sealed class JsonSettingsStore
{
    private readonly AppPathService _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonSettingsStore(AppPathService paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_paths.SettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            return settings is null ? new AppSettings() : Migrate(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "設定ファイルが読み込めないためバックアップを試します。");
            return await LoadBackupAsync(cancellationToken);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.SchemaVersion = 1;
        var temporary = _paths.SettingsFile + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(_paths.SettingsFile))
        {
            File.Copy(_paths.SettingsFile, _paths.BackupFile, true);
        }

        File.Move(temporary, _paths.SettingsFile, true);
    }

    private async Task<AppSettings> LoadBackupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.BackupFile))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_paths.BackupFile);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            return settings is null ? new AppSettings() : Migrate(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "設定バックアップも読み込めませんでした。初期設定で起動します。");
            return new AppSettings();
        }
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        settings.Profiles ??= [];
        if (settings.Profiles.Count == 0)
        {
            settings.Profiles.Add(new RtspProfile());
        }

        settings.SchemaVersion = 1;
        return settings;
    }
}
