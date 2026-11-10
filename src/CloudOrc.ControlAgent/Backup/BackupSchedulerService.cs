using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// Local scheduler: turns each applied policy's schedule into durable QUEUED jobs. It never
/// runs anything itself and never bypasses admission - a created job still has to be GRANTED
/// by the backend scheduler (§3 "Scheduler / Admission") before it starts. This only decides
/// WHEN to ask, using the per-agent stable offset and post-outage spread (§8).
/// </summary>
public sealed class BackupSchedulerService(
    BackupPolicyStore policies,
    BackupJobStore jobs,
    BackupJobRunner runner,
    BackupJobCoordinator coordinator,
    AgentIdentity identity,
    IOptions<BackupOptions> options,
    TimeProvider time,
    ILogger<BackupSchedulerService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    public void Wake() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Max(5, options.Value.SchedulerTickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (EvaluateOnce(time.GetUtcNow(), Random.Shared.NextDouble()) > 0)
                {
                    coordinator.Wake();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup schedule evaluation failed.");
            }

            try
            {
                await _wake.WaitAsync(tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Creates any due jobs. Returns how many were created. Public for tests.</summary>
    public int EvaluateOnce(DateTimeOffset now, double random01)
    {
        var created = 0;
        foreach (var policy in policies.GetAll())
        {
            if (!policy.Enabled || policy.Schedule.Kind == BackupScheduleKinds.Manual)
            {
                continue;
            }

            var state = policies.GetScheduleState(policy.PolicyId);
            var offset = ScheduleCalculator.StableOffset(identity.AgentId, policy.PolicyId, policy.Schedule.WindowMinutes);
            var zone = ScheduleCalculator.ResolveZone(policy.Schedule);
            var due = ScheduleCalculator.Evaluate(policy.Schedule, now, zone, state.LastSlot, offset, random01);
            if (due is null)
            {
                continue;
            }

            if (due.NotBefore == DateTimeOffset.MaxValue)
            {
                policies.SetLastSlot(policy.PolicyId, due.Slot);
                logger.LogInformation("Skipping missed slot {Slot:O} for policy {PolicyId} (runMissed=false).", due.Slot, policy.PolicyId);
                continue;
            }

            SupersedeOlderRuns(policy.PolicyId);

            var (job, isNew) = jobs.CreateIfAbsent(new BackupJobRecord
            {
                JobId = Guid.NewGuid().ToString("D"),
                Kind = BackupJobKinds.Backup,
                PolicyId = policy.PolicyId,
                PolicyVersionId = policy.PolicyVersionId,
                IdempotencyKey = $"sched:{policy.PolicyId}:{due.Slot.UtcDateTime:yyyyMMddTHHmmssZ}",
                CorrelationId = Guid.NewGuid().ToString("D"),
                State = BackupJobStates.Queued,
                NotBefore = due.NotBefore,
                MaxAttempts = policy.MaxAttempts,
                RepositoryId = policy.Repository.RepositoryId,
                Engine = policy.Repository.Engine,
                SpecJson = new JobSpecData { PolicyEffectiveHash = policy.EffectiveHash, Reason = due.IsCatchUp ? "catch-up" : "schedule" }.ToJson(),
                ScheduledSlot = due.Slot,
                CreatedAt = now
            });

            // Recorded only after the job exists; the idempotency key makes a repeat after a crash a no-op.
            policies.SetLastSlot(policy.PolicyId, due.Slot);

            if (isNew)
            {
                created++;
                logger.LogInformation(
                    "Scheduled backup job {JobId} for policy {PolicyId} slot {Slot:O}, starting at {NotBefore:O}{CatchUp}.",
                    job.JobId, policy.PolicyId, due.Slot, due.NotBefore, due.IsCatchUp ? " (catch-up)" : "");
            }
        }

        return created;
    }

    /// <summary>A run still waiting from an older slot is pointless once a newer slot is due.</summary>
    private void SupersedeOlderRuns(string policyId)
    {
        foreach (var old in jobs.GetRecent(200).Where(j =>
                     j.PolicyId == policyId && j.Kind == BackupJobKinds.Backup && BackupJobStates.IsRunnable(j.State)
                     && j.JobId != coordinator.RunningJobId))
        {
            runner.Supersede(old);
        }
    }
}
