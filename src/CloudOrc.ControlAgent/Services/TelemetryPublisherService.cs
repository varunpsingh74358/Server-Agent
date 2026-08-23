using System.Text.Json;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Only runs when BackendConnection is enabled. Every <c>TelemetryIntervalSeconds</c>, if
/// the backend connection is currently up, collects a <see cref="TelemetryCollector"/>
/// snapshot and enqueues it. This is periodic near-real-time telemetry (seconds, not
/// milliseconds) - never the generic command queue, and never PowerShell.
/// </summary>
public sealed class TelemetryPublisherService(
    IOptions<BackendConnectionOptions> options,
    AgentIdentity identity,
    OutgoingMessageChannel outgoing,
    ControlAgentHealthState health,
    TelemetryCollector collector,
    ILogger<TelemetryPublisherService> logger) : BackgroundService
{
    private readonly BackendConnectionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.TelemetryIntervalSeconds));
        logger.LogInformation("Telemetry publisher starting (interval {IntervalSeconds}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PublishIfConnected();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to publish telemetry this cycle.");
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

        logger.LogInformation("Telemetry publisher stopped.");
    }

    private void PublishIfConnected()
    {
        if (health.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState != BackendConnectionState.Connected)
        {
            return;
        }

        var telemetry = collector.Collect(identity);
        outgoing.TryEnqueue(JsonSerializer.Serialize(telemetry, ProtocolJson.Options));
    }
}
