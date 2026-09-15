using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Execution;

namespace CloudOrc.Agent.Contracts.Abstractions;

/// <summary>
/// Applies and verifies the RDP transfer policy (clipboard plain-text-only in both
/// directions, blocked client drive redirection) via Windows-native/RDS policy
/// mechanisms. Parallel to <see cref="IPowerShellExecutor"/>: <c>CommandProcessingService</c>
/// routes CommandTypes.RdpTransferPolicyApply here instead of through the generic
/// PowerShell engine, so this never runs a script and the PowerShell path never needs to
/// know this command type exists.
/// </summary>
public interface IRdpTransferPolicyExecutor
{
    Task<RdpTransferPolicyOutcome> ApplyAsync(CommandRequest request, CancellationToken cancellationToken);
}
