using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Enrollment;

/// <summary>
/// Returned by the enrollment endpoint on success. This is the ONLY place the agent ever
/// learns its backend connection details - nothing about <see cref="BackendUrl"/> is ever
/// hardcoded, manually configured, or baked into the Agent binary/installer.
/// </summary>
public sealed class EnrollmentResponse
{
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    /// <summary>The real-time backend WebSocket endpoint this agent should connect to.</summary>
    [JsonPropertyName("backendUrl")]
    public required string BackendUrl { get; init; }

    /// <summary>
    /// Permanent per-agent authentication credential (presented as a bearer credential on
    /// the WebSocket handshake) - distinct from, and never derived from, the one-time
    /// enrollment secret that was just consumed to obtain it.
    /// </summary>
    [JsonPropertyName("credential")]
    public required string Credential { get; init; }
}
