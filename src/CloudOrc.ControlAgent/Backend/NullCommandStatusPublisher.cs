using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>Used when BackendConnection is disabled - there is nowhere to publish a status to.</summary>
public sealed class NullCommandStatusPublisher : ICommandStatusPublisher
{
    public Task PublishStatusAsync(string commandId, string? correlationId, CommandStatus status, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
