using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Health;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Hosted service that pulls validated commands from every registered
/// <see cref="ICommandSource"/> (today: the local file source and, when backend
/// connectivity is enabled, the WSS source) and hands them to the shared
/// <see cref="ICommandQueue"/>. Each source is consumed independently and concurrently,
/// so a slow/idle source never blocks another. Deliberately does no PowerShell execution
/// itself - detection must never block on a long-running command, which is why it is a
/// separate worker from <see cref="CommandProcessingService"/>.
/// </summary>
public sealed class CommandDetectionService(
    IEnumerable<ICommandSource> commandSources,
    ICommandQueue commandQueue,
    ICommandStatusPublisher statusPublisher,
    ControlAgentHealthState health,
    ILogger<CommandDetectionService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sources = commandSources.ToList();
        logger.LogInformation("Command detection worker starting with {SourceCount} active command source(s).", sources.Count);

        var consumeTasks = sources.Select(source => ConsumeSourceAsync(source, stoppingToken));
        var heartbeatTask = HeartbeatLoopAsync(stoppingToken);

        await Task.WhenAll(consumeTasks.Append(heartbeatTask)).ConfigureAwait(false);

        logger.LogInformation("Command detection worker stopped.");
    }

    private async Task ConsumeSourceAsync(ICommandSource source, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in source.GetCommandsAsync(stoppingToken).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Detected command {CommandId} via {SourceType}; queued for execution.",
                    job.Request.CommandId, source.GetType().Name);

                await commandQueue.EnqueueAsync(job, stoppingToken).ConfigureAwait(false);
                await PublishQueuedStatusAsync(job.Request.CommandId, job.Request.CorrelationId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }
    }

    private async Task PublishQueuedStatusAsync(string commandId, string? correlationId, CancellationToken stoppingToken)
    {
        try
        {
            await statusPublisher.PublishStatusAsync(commandId, correlationId, CommandStatus.Queued, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Publishing a live status update is best-effort - it must never stop the
            // command from actually being queued and executed.
            logger.LogDebug(ex, "Failed to publish QUEUED status for command {CommandId}.", commandId);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            health.TouchDetection();
            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
