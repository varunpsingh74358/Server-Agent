using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.DurableQueue;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// Drains the durable job table one job at a time (bounded resource use, §7) and reconciles
/// jobs interrupted by an agent restart (§15). It is the only thing that starts workers.
/// Wakes immediately when a job is created/cancelled; otherwise sleeps until the next not_before.
/// </summary>
public sealed class BackupJobCoordinator(
    BackupJobStore jobs,
    CommandInboxStore inbox,
    BackupJobRunner runner,
    TimeProvider time,
    ILogger<BackupJobCoordinator> logger) : BackgroundService
{
    private static readonly TimeSpan MaxIdleSleep = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Lock _sync = new();
    private string? _runningJobId;
    private CancellationTokenSource? _runningCts;

    public string? RunningJobId
    {
        get
        {
            lock (_sync)
            {
                return _runningJobId;
            }
        }
    }

    public void Wake() => _wake.Release();

    /// <summary>Cancels the job now if it is the one running; otherwise the runner sees cancel_requested.</summary>
    public void Cancel(string jobId)
    {
        lock (_sync)
        {
            if (_runningJobId == jobId)
            {
                _runningCts?.Cancel();
            }
        }

        Wake();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Reconcile();

        while (!stoppingToken.IsCancellationRequested)
        {
            BackupJobRecord? job;
            try
            {
                job = jobs.NextRunnable(time.GetUtcNow());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not read the job queue.");
                job = null;
            }

            if (job is null)
            {
                await SleepUntilNextAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_sync)
            {
                _runningJobId = job.JobId;
                _runningCts = jobCts;
            }

            try
            {
                logger.LogInformation("Starting {Kind} job {JobId} (attempt {Attempt}).", job.Kind, job.JobId, job.Attempt + 1);
                await runner.RunAsync(job, jobCts.Token, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Service stopping: the job stays in its active state and is reconciled on next start.
                break;
            }
            catch (Exception ex)
            {
                // Never let one job take the hosted service (and with it the whole agent) down.
                logger.LogError(ex, "Job {JobId} ended with an unhandled error; it will be reconciled.", job.JobId);
            }
            finally
            {
                lock (_sync)
                {
                    _runningJobId = null;
                    _runningCts = null;
                }
            }
        }
    }

    private void Reconcile()
    {
        try
        {
            var abandoned = inbox.AbandonInFlight(
                System.Text.Json.JsonSerializer.Serialize(new TypedCommandAcceptance
                {
                    Accepted = false,
                    ReasonCode = BackupReasonCodes.AgentRestarted,
                    Message = "The agent restarted before this command finished; re-send with a new commandId."
                }, Agent.Contracts.Protocol.ProtocolJson.Options),
                time.GetUtcNow());
            if (abandoned > 0)
            {
                logger.LogWarning("{Count} typed command(s) were interrupted by a restart and marked failed.", abandoned);
            }

            runner.CleanupStaleWorkDirectories();
            foreach (var job in jobs.GetInterrupted())
            {
                logger.LogWarning("Reconciling job {JobId} interrupted in state {State}.", job.JobId, job.State);
                runner.ReconcileInterrupted(job);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup job reconciliation failed.");
        }
    }

    private async Task SleepUntilNextAsync(CancellationToken stoppingToken)
    {
        var sleep = MaxIdleSleep;
        try
        {
            if (jobs.EarliestPendingNotBefore() is { } next)
            {
                var until = next - time.GetUtcNow();
                if (until < sleep)
                {
                    sleep = until < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : until;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not compute the next job time.");
        }

        try
        {
            await _wake.WaitAsync(sleep, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
