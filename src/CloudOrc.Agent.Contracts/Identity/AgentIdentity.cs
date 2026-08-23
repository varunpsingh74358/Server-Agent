namespace CloudOrc.Agent.Contracts.Identity;

/// <summary>
/// Identifies this Control Agent instance to the backend. For this local-testing phase,
/// <see cref="AgentId"/> and <see cref="ServerId"/> are locally configured values rather
/// than backend-issued ones - there is no enrollment step yet (see
/// docs/FUTURE_BACKEND_INTEGRATION.md). The shape is deliberately already rich enough
/// that a future enrollment step would only need to populate these same fields
/// differently, not change the protocol.
/// </summary>
public sealed class AgentIdentity
{
    public required string AgentId { get; init; }

    public required string ServerId { get; init; }

    /// <summary>Stable per-machine identifier (Windows MachineGuid where available).</summary>
    public required string MachineId { get; init; }

    public required string MachineName { get; init; }

    public required string AgentVersion { get; init; }

    /// <summary>
    /// Permanent per-agent bearer credential issued during enrollment, or null when this
    /// agent has not been enrolled (local-testing config-based identity only). Never log
    /// or serialize this value - it is only ever read to set the WebSocket handshake's
    /// Authorization header in <c>BackendConnectionService</c>.
    /// </summary>
    public string? Credential { get; init; }
}
