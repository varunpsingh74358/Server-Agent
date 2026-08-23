using System.Text.Json;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Protocol;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Publishes a finished <see cref="CommandResult"/> to the backend as a COMMAND_RESULT
/// message. Every result is sent regardless of which source originated the command - see
/// the architecture doc's "Common Result Pipeline" - so the backend can see the outcome
/// of local-file-sourced commands too, which is useful for local testing/monitoring.
///
/// Delivery guarantee (documented honestly): the message is handed to
/// <see cref="OutgoingMessageChannel"/>, which holds it in memory until
/// <see cref="BackendConnectionService"/> is connected and can send it - so results
/// generated while disconnected are not lost, as long as the agent process itself does
/// not restart before reconnecting. If the process does restart while disconnected, the
/// queued-but-unsent result is lost from the backend's perspective, though it remains
/// visible locally if LocalFileResultSink is also enabled (it writes the same result to
/// results\{commandId}.result.json independently). This is at-least-once-while-the-process-
/// stays-up delivery, not a durable/exactly-once guarantee.
/// </summary>
public sealed class WssResultSink(
    OutgoingMessageChannel outgoing,
    ILogger<WssResultSink> logger) : ICommandResultSink
{
    public Task WriteAsync(CommandResult result, CancellationToken cancellationToken)
    {
        var message = CommandResultMessage.FromCommandResult(result);
        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);

        if (!outgoing.TryEnqueue(json))
        {
            logger.LogWarning("Failed to enqueue COMMAND_RESULT for {CommandId} onto the outgoing channel.", result.CommandId);
        }

        return Task.CompletedTask;
    }
}
