using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Abstractions;

/// <summary>
/// Publishes a non-terminal command status change (QUEUED, RUNNING) as it happens.
/// This is distinct from <see cref="ICommandResultSink"/>, which only ever receives the
/// final, terminal outcome. When no backend connection is configured this is a no-op;
/// when one is, status changes are pushed to the backend for live visibility.
/// </summary>
public interface ICommandStatusPublisher
{
    Task PublishStatusAsync(string commandId, CommandStatus status, CancellationToken cancellationToken);
}
