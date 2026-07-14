using System.Text.Json;
using Microsoft.Extensions.Logging;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Infrastructure.Paths;

namespace RTSPWallpaperStudio.Infrastructure.Settings;

public sealed class RuntimeStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly AppPathService _paths;
    private readonly ILogger<RuntimeStateStore> _logger;

    public RuntimeStateStore(AppPathService paths, ILogger<RuntimeStateStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<RuntimeState> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_paths.RuntimeStateFile))
            {
                return new RuntimeState();
            }

            await using var stream = File.OpenRead(_paths.RuntimeStateFile);
            return await JsonSerializer.DeserializeAsync<RuntimeState>(stream, Options, cancellationToken) ?? new RuntimeState();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "runtime-state.jsonを読み込めないため安全な初期状態で続行します。");
            return new RuntimeState { PreviousShutdownClean = false };
        }
    }

    public async Task SaveAsync(RuntimeState state, CancellationToken cancellationToken = default)
    {
        var tempFile = _paths.RuntimeStateFile + ".tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, state, Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(tempFile, _paths.RuntimeStateFile, overwrite: true);
    }
}
