using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Execution;

/// <summary>
/// Raw result of running a script through the PowerShell execution engine, before it is
/// stamped with a CommandId and handed to a result sink.
/// </summary>
public sealed class PowerShellExecutionOutcome
{
    public required CommandStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required long DurationMilliseconds { get; init; }

    public IReadOnlyList<string> Output { get; init; } = [];

    public string? Error { get; init; }
}
