using System.Text.Json;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Protocol;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Publishes QUEUED/RUNNING status changes to the backend. Best-effort and non-durable by
/// design - unlike a terminal result, a missed status update is superseded by the next
/// one (and ultimately by the terminal COMMAND_RESULT), so there is no need to buffer or
/// retry these specifically.
/// </summary>
public sealed class BackendCommandStatusPublisher(
    OutgoingMessageChannel outgoing,
    ILogger<BackendCommandStatusPublisher> logger) : ICommandStatusPublisher
{
    public Task PublishStatusAsync(string commandId, CommandStatus status, CancellationToken cancellationToken)
    {
        var message = new CommandStatusMessage { CommandId = commandId, Status = status };
        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);

        if (!outgoing.TryEnqueue(json))
        {
            logger.LogDebug("Failed to enqueue {Status} status for command {CommandId}.", status, commandId);
        }

        return Task.CompletedTask;
    }
}
