using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>Backend -> Agent: a liveness probe. The agent responds with a fresh HEARTBEAT.</summary>
public sealed class PingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Ping;
}
