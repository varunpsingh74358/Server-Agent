using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.DurableQueue;

/// <summary>
/// Keeps the durable state bounded (§13 "bounded durable state"): prunes handled inbox rows,
/// finished jobs, dead outbox rows (and their manifest files), and caps the outbox backlog.
/// Runs hourly with low cost; never touches non-terminal jobs or undelivered terminal events.
/// </summary>
public sealed class DurableStateRetentionService(
    CommandInboxStore inbox,
    OutboxStore outbox,
    BackupJobStore jobs,
    IOptions<BackupOptions> options,
    TimeProvider time,
    ILogger<DurableStateRetentionService> logger) : BackgroundService
{
    private readonly BackupOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                RunOnce();
                await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public void RunOnce()
    {
        try
        {
            var now = time.GetUtcNow();
            var inboxPruned = inbox.PruneCompletedBefore(now.AddDays(-_options.InboxRetentionDays));
            var jobsPruned = jobs.PruneTerminalBefore(now.AddDays(-_options.JobRetentionDays));
            var capped = outbox.EnforceCap(_options.MaxOutboxRows);
            foreach (var file in outbox.PruneDeadBefore(now.AddDays(-_options.DeadOutboxRetentionDays)))
            {
                TryDelete(file);
            }

            if (inboxPruned + jobsPruned + capped > 0)
            {
                logger.LogInformation(
                    "Durable state retention: pruned {Inbox} inbox rows, {Jobs} finished jobs, dropped {Capped} stale progress events.",
                    inboxPruned, jobsPruned, capped);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Durable state retention pass failed; will retry next hour.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }
}
