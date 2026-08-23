using CloudOrc.WatchdogAgent.Configuration;
using CloudOrc.WatchdogAgent.ControlAgentManagement;
using CloudOrc.WatchdogAgent.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.WatchdogAgent.Services;

/// <summary>
/// Monitors the Control Agent on a fixed interval and performs controlled, rate-limited
/// recovery when it becomes unhealthy. This is the Watchdog's only job - it never
/// executes arbitrary PowerShell and never accepts command files.
///
/// The relationship is intentionally one-directional (Watchdog -> monitors -> Control
/// Agent): the Watchdog keeps running its monitoring loop regardless of whether the
/// Control Agent is stopped, crashing, or repeatedly failing to recover; the Control
/// Agent has no awareness of the Watchdog at all.
/// </summary>
public sealed class WatchdogMonitorService(
    IOptions<WatchdogOptions> options,
    ControlAgentServiceManager serviceManager,
    ControlAgentHealthClient healthClient,
    ConsecutiveFailureTracker failureTracker,
    RecoveryRateLimiter rateLimiter,
    ILogger<WatchdogMonitorService> logger) : BackgroundService
{
    private readonly WatchdogOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Watchdog monitor starting. Watching service '{ServiceName}', checking every {IntervalSeconds}s, failure threshold {Threshold}.",
            _options.ControlAgentServiceName, _options.HealthCheckIntervalSeconds, _options.ConsecutiveFailureThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single bad monitoring cycle (e.g. a transient WMI/service query error)
                // must never stop the Watchdog from continuing to watch.
                logger.LogError(ex, "Unexpected error during a monitoring cycle; will retry next cycle.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Watchdog monitor stopped.");
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var serviceStatus = serviceManager.TryGetStatus();
        logger.LogInformation(
            "Control Agent service '{ServiceName}' status: {Status}.",
            _options.ControlAgentServiceName, serviceStatus?.ToString() ?? "NotInstalled");

        var health = await healthClient.TryGetHealthAsync(stoppingToken).ConfigureAwait(false);
        var healthy = health is { Status: "HEALTHY" };

        if (health is not null)
        {
            // backendConnectionState is logged for visibility only - it is never a
            // factor in the healthy/unhealthy decision below. A disconnected backend is
            // not a Control Agent failure; see docs/ARCHITECTURE.md.
            logger.LogInformation(
                "Health check response: status={Status}, detectionWorkerAlive={DetectionAlive}, processingWorkerAlive={ProcessingAlive}, currentCommandId={CommandId}, backendConnectionState={BackendState}.",
                health.Status, health.DetectionWorkerAlive, health.ProcessingWorkerAlive, health.CurrentCommandId ?? "(none)", health.BackendConnectionState);
        }
        else
        {
            logger.LogWarning("Health check did not get a response from the Control Agent.");
        }

        if (healthy)
        {
            var previousFailures = failureTracker.RecordSuccess();
            if (previousFailures > 0)
            {
                logger.LogInformation("Control Agent is healthy again; consecutive failure count reset from {Previous} to 0.", previousFailures);
            }
            return;
        }

        var failureCount = failureTracker.RecordFailure();
        logger.LogWarning(
            "Control Agent health check failed ({Count}/{Threshold}). Reason: {Reason}",
            failureCount, _options.ConsecutiveFailureThreshold,
            health is null ? "no response from health pipe" : $"reported status '{health.Status}'");

        if (!failureTracker.HasReachedThreshold(_options.ConsecutiveFailureThreshold))
        {
            return;
        }

        await AttemptRecoveryAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task AttemptRecoveryAsync(CancellationToken stoppingToken)
    {
        var decision = rateLimiter.Evaluate();
        if (!decision.Allowed)
        {
            logger.LogWarning("Recovery is needed but was suppressed: {Reason}", decision.Reason);
            return;
        }

        logger.LogWarning(
            "Consecutive failure threshold reached; attempting recovery of service '{ServiceName}'.",
            _options.ControlAgentServiceName);

        rateLimiter.RecordAttempt();
        var restartIssued = serviceManager.TryRestart();

        if (!restartIssued)
        {
            rateLimiter.RecordOutcome(succeeded: false);
            logger.LogError("Recovery attempt failed: the service could not be restarted (see the warning above for why).");
            return;
        }

        logger.LogInformation("Restart issued; waiting {WaitSeconds}s before re-checking health.", _options.RecoveryWaitSeconds);
        await Task.Delay(TimeSpan.FromSeconds(_options.RecoveryWaitSeconds), stoppingToken).ConfigureAwait(false);

        var postRecoveryHealth = await healthClient.TryGetHealthAsync(stoppingToken).ConfigureAwait(false);
        var postRecoveryHealthy = postRecoveryHealth is { Status: "HEALTHY" };
        rateLimiter.RecordOutcome(postRecoveryHealthy);

        if (postRecoveryHealthy)
        {
            logger.LogInformation("Recovery succeeded; Control Agent is healthy again.");
            failureTracker.RecordSuccess();
        }
        else
        {
            logger.LogError("Recovery attempt did not restore health; the next attempt will be subject to backoff.");
        }
    }
}
