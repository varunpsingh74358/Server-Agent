using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Health;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Hosted service that sequentially drains the <see cref="ICommandQueue"/>, executes each
/// job through the generic <see cref="IPowerShellExecutor"/>, persists the result to
/// every registered <see cref="ICommandResultSink"/> (today: local file, and when backend
/// connectivity is enabled, WSS), and acknowledges the job back to whichever source
/// produced it (<see cref="CommandJob.OriginSource"/>). "Sequentially" is a deliberate
/// design choice for this first version - one <c>await foreach</c> over the queue, no
/// parallel execution - so a single misbehaving command can never starve or race with
/// another, regardless of which source it came from.
/// </summary>
public sealed class CommandProcessingService(
    ICommandQueue commandQueue,
    IEnumerable<ICommandResultSink> resultSinks,
    ICommandStatusPublisher statusPublisher,
    IPowerShellExecutor executor,
    ControlAgentHealthState health,
    ILogger<CommandProcessingService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Command processing worker starting.");

        var processTask = ProcessQueueAsync(stoppingToken);
        var heartbeatTask = HeartbeatLoopAsync(stoppingToken);

        await Task.WhenAll(processTask, heartbeatTask).ConfigureAwait(false);

        logger.LogInformation("Command processing worker stopped.");
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in commandQueue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessOneAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }
    }

    private async Task ProcessOneAsync(CommandJob job, CancellationToken stoppingToken)
    {
        var commandId = job.Request.CommandId;
        health.SetCurrentCommand(commandId, CommandStatus.Running);
        health.TouchProcessing();

        await PublishStatusAsync(commandId, CommandStatus.Running, stoppingToken).ConfigureAwait(false);

        logger.LogInformation(
            "Executing command {CommandId} (timeout {TimeoutSeconds}s).",
            commandId, job.EffectiveTimeoutSeconds);

        CommandResult result;
        try
        {
            var outcome = await executor
                .ExecuteAsync(job.Request.Script, TimeSpan.FromSeconds(job.EffectiveTimeoutSeconds), stoppingToken)
                .ConfigureAwait(false);

            result = new CommandResult
            {
                CommandId = commandId,
                Status = outcome.Status,
                StartedAt = outcome.StartedAt,
                CompletedAt = outcome.CompletedAt,
                DurationMilliseconds = outcome.DurationMilliseconds,
                Output = outcome.Output,
                Error = outcome.Error
            };
        }
        catch (Exception ex)
        {
            // Defense in depth: even if the executor itself throws unexpectedly, one bad
            // command must never take down the processing worker.
            logger.LogError(ex, "Unexpected error executing command {CommandId}.", commandId);
            var now = DateTimeOffset.UtcNow;
            result = new CommandResult
            {
                CommandId = commandId,
                Status = CommandStatus.Failed,
                StartedAt = now,
                CompletedAt = now,
                DurationMilliseconds = 0,
                Output = [],
                Error = $"Unhandled agent exception: {ex.Message}"
            };
        }

        foreach (var sink in resultSinks)
        {
            try
            {
                await sink.WriteAsync(result, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One sink failing to persist/send a result must never stop the others,
                // and must never be mistaken for the command itself having failed.
                logger.LogError(ex, "Result sink {SinkType} failed to write the result for command {CommandId}.", sink.GetType().Name, commandId);
            }
        }

        var succeeded = result.Status == CommandStatus.Success;
        await job.OriginSource.AcknowledgeAsync(job, succeeded, stoppingToken).ConfigureAwait(false);

        health.RecordCompletion(failed: !succeeded);
        health.ClearCurrentCommand();
        health.TouchProcessing();

        logger.LogInformation(
            "Command {CommandId} finished with status {Status} in {DurationMs}ms.{ErrorSuffix}",
            commandId, result.Status, result.DurationMilliseconds,
            result.Error is null ? string.Empty : $" Error: {result.Error}");
    }

    private async Task PublishStatusAsync(string commandId, CommandStatus status, CancellationToken stoppingToken)
    {
        try
        {
            await statusPublisher.PublishStatusAsync(commandId, status, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish {Status} status for command {CommandId}.", status, commandId);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            health.TouchProcessing();
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
