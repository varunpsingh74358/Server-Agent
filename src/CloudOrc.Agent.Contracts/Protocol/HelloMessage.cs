using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>Sent once, immediately after the WebSocket connection is established.</summary>
public sealed class HelloMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Hello;

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    [JsonPropertyName("machineId")]
    public required string MachineId { get; init; }

    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("agentVersion")]
    public required string AgentVersion { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
