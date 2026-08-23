using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Abstractions;

/// <summary>
/// In-memory hand-off between command detection (producer) and command execution
/// (single sequential consumer). Deliberately not backed by any durable storage - if the
/// process restarts, queued-but-not-yet-executed jobs are gone; recovery relies on the
/// command source re-discovering unprocessed work on the file system, not on this queue.
/// </summary>
public interface ICommandQueue
{
    ValueTask EnqueueAsync(CommandJob job, CancellationToken cancellationToken);

    IAsyncEnumerable<CommandJob> DequeueAllAsync(CancellationToken cancellationToken);
}
