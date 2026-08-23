using CloudOrc.Agent.Contracts.Execution;

namespace CloudOrc.Agent.Contracts.Abstractions;

/// <summary>
/// Generic PowerShell execution engine. It accepts an arbitrary script string and must
/// never contain command-specific (business) logic - new PowerShell commands must be
/// usable without changing this contract or its implementation.
/// </summary>
public interface IPowerShellExecutor
{
    Task<PowerShellExecutionOutcome> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken);
}
