using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.DurableQueue;

namespace CloudOrc.ControlAgent.Commands;

/// <summary>backup.policy.apply - stores the backend-resolved effective policy (§11 backup_assignment).</summary>
public sealed class PolicyApplyHandler(
    BackupPolicyStore policies,
    BackupSchedulerService scheduler,
    AgentIdentity identity,
    TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.PolicyApply;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var policy = context.Parameters<PolicyApplyParameters>()?.Policy;
        var errors = BackupPolicyValidator.Validate(policy);
        if (errors.Count > 0)
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.PolicyInvalid, string.Join(" ", errors.Take(5))));
        }

        var existing = policies.Get(policy!.PolicyId);
        if (existing is not null && existing.PolicyVersionId == policy.PolicyVersionId && existing.EffectiveHash == policy.EffectiveHash)
        {
            return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = "Policy version already applied." });
        }

        var now = time.GetUtcNow();
        policies.Upsert(policy, now);

        // A brand-new policy (or a changed schedule) must not "catch up" a slot that passed before
        // it was applied - the first run is the next slot from now.
        if (existing is null || !SameSchedule(existing.Schedule, policy.Schedule))
        {
            var offset = ScheduleCalculator.StableOffset(identity.AgentId, policy.PolicyId, policy.Schedule.WindowMinutes);
            var latest = ScheduleCalculator.LatestSlotAtOrBefore(policy.Schedule, now - offset, ScheduleCalculator.ResolveZone(policy.Schedule));
            if (latest is not null)
            {
                policies.SetLastSlot(policy.PolicyId, latest.Value);
            }
        }

        scheduler.Wake();
        return Task.FromResult(new TypedCommandAcceptance
        {
            Accepted = true,
            Message = $"Policy {policy.PolicyId} version {policy.PolicyVersionId} applied."
        });
    }

    private static bool SameSchedule(BackupSchedule a, BackupSchedule b) =>
        a.Kind == b.Kind && a.IntervalHours == b.IntervalHours && a.WindowMinutes == b.WindowMinutes && a.TimeZoneId == b.TimeZoneId
        && a.TimesOfDay.SequenceEqual(b.TimesOfDay) && a.DaysOfWeek.SequenceEqual(b.DaysOfWeek);
}

/// <summary>backup.policy.remove - stops scheduling and cancels that policy's pending runs.</summary>
public sealed class PolicyRemoveHandler(BackupPolicyStore policies, BackupJobStore jobs, BackupJobCoordinator coordinator) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.PolicyRemove;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var policyId = context.Parameters<PolicyRemoveParameters>()?.PolicyId;
        if (!BackupPolicyValidator.IsSafeId(policyId))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.PolicyInvalid, "policyId is required."));
        }

        var removed = policies.Remove(policyId!);
        foreach (var job in jobs.GetRecent(500).Where(j => j.PolicyId == policyId && !BackupJobStates.IsTerminal(j.State)))
        {
            if (jobs.RequestCancel(job.JobId))
            {
                coordinator.Cancel(job.JobId);
            }
        }

        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = removed ? "Policy removed." : "Policy was not applied." });
    }
}

/// <summary>backup.run - on-demand backup; the backend-issued jobId is the idempotency key.</summary>
public sealed class BackupRunHandler(BackupPolicyStore policies, BackupJobStore jobs, BackupJobCoordinator coordinator, TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.Run;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<BackupRunParameters>();
        if (!BackupPolicyValidator.IsSafeId(p?.JobId) || !BackupPolicyValidator.IsSafeId(p?.PolicyId))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.PolicyInvalid, "jobId and policyId are required."));
        }

        var policy = policies.Get(p!.PolicyId!);
        if (policy is null || !policy.Enabled)
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.PolicyNotFound, "That policy is not applied to this endpoint."));
        }

        var now = time.GetUtcNow();
        var (job, _) = jobs.CreateIfAbsent(new BackupJobRecord
        {
            JobId = p.JobId!,
            Kind = BackupJobKinds.Backup,
            PolicyId = policy.PolicyId,
            PolicyVersionId = policy.PolicyVersionId,
            IdempotencyKey = $"run:{p.JobId}",
            CorrelationId = context.CorrelationId ?? context.CommandId,
            State = BackupJobStates.Queued,
            NotBefore = now,
            MaxAttempts = policy.MaxAttempts,
            RepositoryId = policy.Repository.RepositoryId,
            Engine = policy.Repository.Engine,
            SpecJson = new JobSpecData { PolicyEffectiveHash = policy.EffectiveHash, Reason = p.Reason ?? "on-demand", Actor = context.Actor }.ToJson(),
            CreatedAt = now
        });

        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, JobId = job.JobId, Message = $"Backup job {job.State.ToLowerInvariant()}." });
    }
}

/// <summary>backup.cancel - cancels a queued or running job of any kind.</summary>
public sealed class JobCancelHandler(BackupJobStore jobs, BackupJobCoordinator coordinator) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.Cancel;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var jobId = context.Parameters<JobCancelParameters>()?.JobId;
        if (!BackupPolicyValidator.IsSafeId(jobId))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.PolicyInvalid, "jobId is required."));
        }

        var job = jobs.Get(jobId!);
        if (job is null)
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected("JOB_NOT_FOUND", "No such job on this endpoint."));
        }

        if (BackupJobStates.IsTerminal(job.State))
        {
            return Task.FromResult(new TypedCommandAcceptance { Accepted = true, JobId = job.JobId, Message = $"Job already {job.State}." });
        }

        jobs.RequestCancel(job.JobId);
        coordinator.Cancel(job.JobId);
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, JobId = job.JobId, Message = "Cancellation requested." });
    }
}

/// <summary>backup.verify - engine-native verification of an existing recovery point.</summary>
public sealed class VerifyHandler(BackupJobStore jobs, BackupJobCoordinator coordinator, TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.Verify;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<VerifyParameters>();
        if (p is null || !BackupPolicyValidator.IsSafeId(p.JobId) || !BackupPolicyValidator.IsSafeId(p.RepositoryId)
            || p.Engine is not (BackupEngineNames.Kopia or BackupEngineNames.Restic) || !IsEngineSnapshotId(p.EngineSnapshotId)
            || p.VerifyFilesPercent is < 0 or > 100)
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.PolicyInvalid,
                "jobId, repositoryId, engine, engineSnapshotId and verifyFilesPercent (0-100) are required."));
        }

        var job = CreateJob(jobs, context, BackupJobKinds.Verify, p.JobId!, p.RepositoryId!, p.Engine!, new JobSpecData { Verify = p, Actor = context.Actor }, time);
        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, JobId = job.JobId, Message = "Verification queued." });
    }

    internal static bool IsEngineSnapshotId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && id.All(char.IsAsciiLetterOrDigit);

    internal static BackupJobRecord CreateJob(BackupJobStore jobs, TypedCommandContext context, string kind, string jobId, string repositoryId, string engine, JobSpecData spec, TimeProvider time)
    {
        var now = time.GetUtcNow();
        return jobs.CreateIfAbsent(new BackupJobRecord
        {
            JobId = jobId,
            Kind = kind,
            IdempotencyKey = $"{kind}:{jobId}",
            CorrelationId = context.CorrelationId ?? context.CommandId,
            State = BackupJobStates.Queued,
            NotBefore = now,
            MaxAttempts = 3,
            RepositoryId = repositoryId,
            Engine = engine,
            SpecJson = spec.ToJson(),
            CreatedAt = now
        }).Job;
    }
}

/// <summary>backup.maintenance - throttled retention prune / compaction (§7, §9 immutability caution).</summary>
public sealed class MaintenanceHandler(BackupJobStore jobs, BackupJobCoordinator coordinator, TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.Maintenance;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<MaintenanceParameters>();
        if (p is null || !BackupPolicyValidator.IsSafeId(p.JobId) || !BackupPolicyValidator.IsSafeId(p.RepositoryId)
            || p.Engine is not (BackupEngineNames.Kopia or BackupEngineNames.Restic)
            || (p.PolicyId is not null && !BackupPolicyValidator.IsSafeId(p.PolicyId)))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.PolicyInvalid, "jobId, repositoryId and engine are required."));
        }

        var job = VerifyHandler.CreateJob(jobs, context, BackupJobKinds.Maintenance, p.JobId!, p.RepositoryId!, p.Engine!,
            new JobSpecData { Maintenance = p, Actor = context.Actor }, time);
        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, JobId = job.JobId, Message = "Maintenance queued." });
    }
}

/// <summary>
/// restore.execute - only ever sent after the backend's approval workflow (§5 Restore drawer,
/// §10 "Restore is separately authorized"). The agent independently re-validates the target and
/// every selected path (§10 path traversal / symlink escape) - the worker checks again on disk.
/// </summary>
public sealed class RestoreExecuteHandler(BackupJobStore jobs, BackupJobCoordinator coordinator, TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.RestoreExecute;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<RestoreExecuteParameters>();
        var error = Validate(p);
        if (error is not null)
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(BackupReasonCodes.RestoreTargetInvalid, error));
        }

        var job = VerifyHandler.CreateJob(jobs, context, BackupJobKinds.Restore, p!.RestoreJobId!, p.RepositoryId!, p.Engine!,
            new JobSpecData { Restore = p, Actor = context.Actor }, time);
        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, JobId = job.JobId, Message = "Restore queued." });
    }

    public static string? Validate(RestoreExecuteParameters? p)
    {
        if (p is null || !BackupPolicyValidator.IsSafeId(p.RestoreJobId) || !BackupPolicyValidator.IsSafeId(p.RepositoryId)
            || !BackupPolicyValidator.IsSafeId(p.RecoveryPointId) || p.Engine is not (BackupEngineNames.Kopia or BackupEngineNames.Restic)
            || !VerifyHandler.IsEngineSnapshotId(p.EngineSnapshotId))
        {
            return "restoreJobId, recoveryPointId, repositoryId, engine and engineSnapshotId are required.";
        }

        if (!BackupPolicyValidator.IsAllowedLocalPath(p.SourceRoot))
        {
            return "sourceRoot must be an absolute local path.";
        }

        if (p.TargetKind is not (RestoreTargetKinds.Original or RestoreTargetKinds.Alternate))
        {
            return "targetKind must be 'original' or 'alternate'.";
        }

        if (p.TargetKind == RestoreTargetKinds.Alternate && !BackupPolicyValidator.IsAllowedLocalPath(p.TargetPath))
        {
            return "targetPath must be an absolute local path for an alternate restore.";
        }

        if (p.Mode is not (RestoreModes.Overwrite or RestoreModes.Rename or RestoreModes.SkipExisting))
        {
            return "mode must be 'overwrite', 'rename' or 'skip-existing'.";
        }

        if (p.Selection.Count > 10_000)
        {
            return "At most 10000 selected paths per restore.";
        }

        foreach (var item in p.Selection)
        {
            if (!IsSafeRelativePath(item))
            {
                return $"Selected path '{(item.Length > 60 ? item[..60] : item)}' is not a safe relative path.";
            }
        }

        return null;
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Any(char.IsControl))
        {
            return false;
        }

        return path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).All(s => s is not (".." or "."));
    }
}
