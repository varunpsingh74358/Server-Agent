using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>Non-terminal status change (QUEUED, RUNNING) for a specific command.</summary>
public sealed class CommandStatusMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.CommandStatus;

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CommandStatus Status { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
