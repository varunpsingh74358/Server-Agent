using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>
/// Lightweight, periodic liveness signal. Deliberately cheap to produce - it reads the
/// Control Agent's existing in-memory health state, it never runs PowerShell.
/// </summary>
public sealed class HeartbeatMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Heartbeat;

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("workerAlive")]
    public required bool WorkerAlive { get; init; }

    [JsonPropertyName("currentCommandId")]
    public string? CurrentCommandId { get; init; }

    [JsonPropertyName("currentCommandStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CommandStatus? CurrentCommandStatus { get; init; }

    [JsonPropertyName("lastActivityAt")]
    public required DateTimeOffset LastActivityAt { get; init; }
}
