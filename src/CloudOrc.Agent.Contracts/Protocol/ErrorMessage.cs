using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>Protocol-level error, e.g. a rejected/malformed COMMAND message.</summary>
public sealed class ErrorMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Error;

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("relatedCommandId")]
    public string? RelatedCommandId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
