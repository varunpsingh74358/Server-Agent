using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Execution;

/// <summary>
/// Raw result of applying and verifying the RDP transfer policy, before it is stamped with
/// a CommandId and handed to a result sink. Mirrors <see cref="PowerShellExecutionOutcome"/>
/// so <c>CommandProcessingService</c> can map either shape into a <see cref="CommandResult"/>
/// the same way; <see cref="Output"/> carries human-readable diagnostic lines (OS build,
/// detected capability, desired vs. actual state, requiresNewSession) rather than script
/// stdout.
/// </summary>
public sealed class RdpTransferPolicyOutcome
{
    public required CommandStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required long DurationMilliseconds { get; init; }

    public IReadOnlyList<string> Output { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>
    /// True if an already-connected RDP session will not reflect this policy until it
    /// reconnects (the normal case for RDS clipboard/drive redirection policies, which are
    /// negotiated at session connect time). False only when nothing needed to change
    /// because the machine was already compliant with the desired state.
    /// </summary>
    public bool RequiresNewSession { get; init; }
}
