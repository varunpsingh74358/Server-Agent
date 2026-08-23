using System.Text.Json;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>Shared JSON conventions for the backend protocol: camelCase, UTC timestamps.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads only the "type" discriminator from a raw protocol message without fully
    /// deserializing it, so the caller can pick the right concrete type. Returns null if
    /// the JSON is malformed or has no "type" property - callers must treat that as an
    /// invalid/unrecognized message, never throw it away silently without logging.
    /// </summary>
    public static string? TryReadMessageType(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
