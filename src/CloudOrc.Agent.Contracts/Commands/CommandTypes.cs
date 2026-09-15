namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// The set of <see cref="CommandRequest.CommandType"/>/<see cref="Protocol.CommandMessage.CommandType"/>
/// values the agent understands. Centralized here so the backend dispatch branch, the
/// local file source (which trusts whatever commandType is in the JSON it reads), and the
/// validator all agree on the exact strings.
/// </summary>
public static class CommandTypes
{
    public const string PowerShellExec = "powershell-exec";

    /// <summary>
    /// Applies the RDP transfer policy (clipboard plain-text-only in both directions, and
    /// blocked client drive redirection) via Windows-native/RDS policy mechanisms. See
    /// <c>RdpTransferPolicy</c> in CloudOrc.ControlAgent.
    /// </summary>
    public const string RdpTransferPolicyApply = "security.rdp.transfer.apply";
}
