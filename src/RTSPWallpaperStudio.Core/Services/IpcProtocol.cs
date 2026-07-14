using System.Text.Json;
using System.Text.Json.Serialization;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Core.Services;

public static class IpcProtocol
{
    public const int MaxMessageLength = 64 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    static IpcProtocol()
    {
        Options.Converters.Add(new NintJsonConverter());
    }

    public static string Serialize(IpcEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static string Serialize(IpcMessage message) => JsonSerializer.Serialize(message, Options);

    public static string SerializePayload<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    public static T? DeserializePayload<T>(string payload) => JsonSerializer.Deserialize<T>(payload, Options);

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

    public static bool TryDeserializeMessage(string text, out IpcMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxMessageLength)
        {
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<IpcMessage>(text, Options);
            return message is not null && message.SchemaVersion == 1 && message.Name.Length <= 96;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class NintJsonConverter : JsonConverter<nint>
    {
        public override nint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (nint)reader.GetInt64();

        public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.ToInt64());
    }
}
