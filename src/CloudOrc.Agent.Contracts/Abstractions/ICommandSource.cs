using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Abstractions;

/// <summary>
/// Produces validated, de-duplicated <see cref="CommandJob"/> instances for the agent to
/// execute. This is the transport boundary: today <c>LocalFileCommandSource</c> polls a
/// local directory, in the future a <c>SecureWebSocketCommandSource</c> would receive
/// commands from the backend. Nothing downstream of this interface needs to change when
/// the transport changes.
/// </summary>
public interface ICommandSource
{
    /// <summary>
    /// Streams validated commands as they become available. Implementations are
    /// responsible for detecting new work, validating it, and applying duplicate
    /// protection before a job is ever yielded here.
    /// </summary>
    IAsyncEnumerable<CommandJob> GetCommandsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Called exactly once per job after execution has finished (successfully or not),
    /// so the source can finalize its bookkeeping (e.g. moving a file to completed/failed).
    /// </summary>
    Task AcknowledgeAsync(CommandJob job, bool succeeded, CancellationToken cancellationToken);
}
