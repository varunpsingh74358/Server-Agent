namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// Lifecycle states a command moves through from submission to completion.
/// </summary>
public enum CommandStatus
{
    Queued,
    Running,
    Success,
    Failed,
    Timeout,
    Cancelled
}
