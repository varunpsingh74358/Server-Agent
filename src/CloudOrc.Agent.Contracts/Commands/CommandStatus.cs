namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// Lifecycle states a command moves through from submission to completion.
///
/// <see cref="Partial"/> and <see cref="Unsupported"/> exist for commands that verify
/// actual device/OS state against a desired state (e.g. CommandTypes.RdpTransferPolicyApply)
/// rather than just running a script to completion: <see cref="Partial"/> means some but
/// not all of the requested state was achieved and verified (typically because part of it
/// is not supported on this OS build), and <see cref="Unsupported"/> means none of it is
/// achievable on this machine. Neither is produced by the PowerShell execution path.
/// </summary>
public enum CommandStatus
{
    Queued,
    Running,
    Success,
    Failed,
    Timeout,
    Cancelled,
    Partial,
    Unsupported
}
