namespace CloudOrc.Agent.Contracts.Enrollment;

/// <summary>
/// What the Control Agent persists locally (DPAPI-protected, machine scope) after a
/// successful enrollment. This - not appsettings.json - is the source of truth for
/// AgentId/ServerId/BackendUrl/credential once an agent is enrolled; it survives reboots
/// and service restarts without any manual configuration or re-enrollment.
/// </summary>
public sealed class EnrolledAgentState
{
    public required string AgentId { get; init; }

    public required string ServerId { get; init; }

    public required string BackendUrl { get; init; }

    public required string Credential { get; init; }

    public required DateTimeOffset EnrolledAtUtc { get; init; }
}
