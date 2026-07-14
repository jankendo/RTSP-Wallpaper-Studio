using System.Text.Json;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Core.Services;

public static class IpcProtocol
{
    public const int MaxMessageLength = 64 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize(IpcEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static bool TryDeserialize(string text, out IpcEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxMessageLength)
        {
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<IpcEnvelope>(text, Options);
            return envelope is not null && envelope.Command.Length <= 64;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
