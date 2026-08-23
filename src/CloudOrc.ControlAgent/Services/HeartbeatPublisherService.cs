using System.Text.Json;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Only runs when BackendConnection is enabled (see Program.cs). Every
/// <c>HeartbeatIntervalSeconds</c>, if the backend connection is currently up, builds and
/// enqueues a HEARTBEAT built directly from the existing <see cref="ControlAgentHealthState"/>
/// snapshot - the same state HealthPipeServer already exposes to the Watchdog. Never runs
/// PowerShell; skipped entirely while disconnected rather than queuing up stale heartbeats.
/// </summary>
public sealed class HeartbeatPublisherService(
    IOptions<BackendConnectionOptions> options,
    AgentIdentity identity,
    OutgoingMessageChannel outgoing,
    ControlAgentHealthState health,
    ILogger<HeartbeatPublisherService> logger) : BackgroundService
{
    private readonly BackendConnectionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));
        logger.LogInformation("Heartbeat publisher starting (interval {IntervalSeconds}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PublishIfConnected();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to publish a heartbeat this cycle.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Heartbeat publisher stopped.");
    }

    private void PublishIfConnected()
    {
        var snapshot = health.Snapshot(TimeSpan.FromSeconds(30));
        if (snapshot.BackendConnectionState != BackendConnectionState.Connected)
        {
            return;
        }

        var lastActivity = snapshot.LastProcessingActivityAt > snapshot.LastDetectionActivityAt
            ? snapshot.LastProcessingActivityAt
            : snapshot.LastDetectionActivityAt;

        var heartbeat = new HeartbeatMessage
        {
            AgentId = identity.AgentId,
            ServerId = identity.ServerId,
            Status = snapshot.Status,
            WorkerAlive = snapshot.DetectionWorkerAlive && snapshot.ProcessingWorkerAlive,
            CurrentCommandId = snapshot.CurrentCommandId,
            CurrentCommandStatus = snapshot.CurrentCommandStatus,
            LastActivityAt = lastActivity
        };

        outgoing.TryEnqueue(JsonSerializer.Serialize(heartbeat, ProtocolJson.Options));
    }
}
