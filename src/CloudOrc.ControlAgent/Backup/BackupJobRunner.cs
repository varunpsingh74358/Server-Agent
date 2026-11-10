using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.ResourceGovernor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// Executes ONE durable job end to end, following CO-DEV-BKP-001:
/// <list type="number">
/// <item>Admission (§8, §12): ask for a lease; DEFERRED → wait not_before + jitter; DENIED → fail.</item>
/// <item>Storage grant (§9, §10): short-lived, repository/operation-scoped credentials, memory only.</item>
/// <item>Budget (§7): profile × lease limits → Job Object caps + engine knobs.</item>
/// <item>Worker (§3, §13): isolated process; stages QUEUED→SNAPSHOTTING→SCANNING→TRANSFERRING→VERIFYING.</item>
/// <item>Lease renewal with fencing token; lease lost → stop; grant renewed on worker request.</item>
/// <item>Outcome: SUCCEEDED/PARTIAL/FAILED/CANCELLED, or RETRYING with bounded full-jitter backoff.</item>
/// </list>
/// Every step is durable: a crash at any point leaves a job the coordinator reconciles on restart.
/// </summary>
public sealed class BackupJobRunner(
    BackupJobStore jobs,
    BackupPolicyStore policies,
    JobEventRecorder events,
    IBackupControlPlane controlPlane,
    IResourceGovernor governor,
    IWorkerHost workerHost,
    BackupPaths paths,
    OutboxStore outbox,
    AgentIdentity identity,
    IOptions<BackupOptions> options,
    TimeProvider time,
    ILogger<BackupJobRunner> logger)
{
    private readonly BackupOptions _options = options.Value;

    // Accessors for the nested worker-callback class (primary-constructor parameters are not visible to it).
    private CancellationToken _serviceStopping;

    private JobEventRecorder Events => events;
    private BackupJobStore Jobs => jobs;
    private TimeProvider Time => time;
    private IBackupControlPlane ControlPlane => controlPlane;
    private ILogger Logger => logger;

    public async Task RunAsync(BackupJobRecord job, CancellationToken jobCancellation, CancellationToken serviceStopping = default)
    {
        _serviceStopping = serviceStopping;
        try
        {
            await RunCoreAsync(job, jobCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serviceStopping.IsCancellationRequested)
        {
            // Agent is stopping (upgrade, reboot, restart): leave the job in its current state so
            // the next start reconciles and retries it (§15) - never record it as cancelled.
            logger.LogWarning("Agent stopping while job {JobId} was running; it will be reconciled on restart.", job.JobId);
        }
        catch (OperationCanceledException)
        {
            // Operator cancel arriving while a control-plane call or the worker was in flight.
            var current = jobs.Get(job.JobId) ?? job;
            if (!BackupJobStates.IsTerminal(current.State))
            {
                Finish(current, BackupJobStates.Cancelled, BackupReasonCodes.Cancelled, "Cancelled.", CancelledEvent(current), null);
            }
        }
        catch (Exception ex)
        {
            // Defence in depth: one job's bug must never take the coordinator down.
            logger.LogError(ex, "Job {JobId} failed with an unexpected agent error.", job.JobId);
            var current = jobs.Get(job.JobId) ?? job;
            if (!BackupJobStates.IsTerminal(current.State))
            {
                RetryOrFail(current, BackupReasonCodes.EngineFailed, "Unexpected agent error.");
            }
        }
        finally
        {
            events.Forget(job.JobId);
            TryDeleteDirectory(paths.WorkFor(job.JobId));
        }
    }

    private async Task RunCoreAsync(BackupJobRecord job, CancellationToken jobCancellation)
    {
        if (job.CancelRequested || jobCancellation.IsCancellationRequested)
        {
            Finish(job, BackupJobStates.Cancelled, BackupReasonCodes.Cancelled, "Cancelled before start.", CancelledEvent(job), null);
            return;
        }

        var context = ResolveContext(job);
        if (context.Error is not null)
        {
            Finish(job, BackupJobStates.Failed, context.ErrorCode, context.Error, FailedEvent(job), null);
            return;
        }

        if (!controlPlane.IsConfigured)
        {
            Defer(job, BackupReasonCodes.ControlPlaneUnreachable, "Control plane is not configured.", null);
            return;
        }

        // ---------------------------------------------------------------- 1. admission
        var admission = await controlPlane.RequestAdmissionAsync(new AdmissionRequest
        {
            JobId = job.JobId,
            EndpointId = identity.AgentId,
            PolicyVersion = job.PolicyVersionId,
            EstimatedBytes = context.EstimatedBytes,
            IdempotencyKey = job.IdempotencyKey,
            Operation = job.Kind,
            RepositoryId = context.RepositoryId,
            ResourceProfile = context.Profile
        }, jobCancellation).ConfigureAwait(false);

        if (admission.Status == CallStatus.Transient)
        {
            Defer(job, BackupReasonCodes.ControlPlaneUnreachable, $"Admission unavailable ({admission.Detail}).", admission.RetryAfter);
            return;
        }

        if (admission.Status == CallStatus.Rejected || admission.Value is null)
        {
            var code = admission.Code is System.Net.HttpStatusCode.Forbidden ? BackupReasonCodes.AuthorizationRejected : BackupReasonCodes.AdmissionDenied;
            Finish(job, BackupJobStates.Failed, code, $"Admission refused ({admission.Detail}).", FailedEvent(job), null);
            return;
        }

        var decision = admission.Value;
        switch (decision.Decision)
        {
            case AdmissionDecisions.Deferred:
            {
                var jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Max(0, decision.JitterWindowSeconds ?? 0));
                var notBefore = (decision.NotBefore ?? time.GetUtcNow().AddMinutes(_options.ControlPlaneUnreachableRetryMinutes)) + jitter;
                DeferUntil(job, BackupReasonCodes.AdmissionDeferred, decision.Reason ?? "Capacity is busy - deferred by the scheduler.", notBefore);
                return;
            }

            case AdmissionDecisions.Granted when decision.Lease is not null:
                break;

            default:
                Finish(job, BackupJobStates.Failed, BackupReasonCodes.AdmissionDenied, decision.Reason ?? "Admission denied.", FailedEvent(job), null);
                return;
        }

        var lease = decision.Lease!;

        // Re-check: the job may have been cancelled/superseded while admission was in flight.
        var latest = jobs.Get(job.JobId);
        if (latest is null || !BackupJobStates.IsRunnable(latest.State) || latest.CancelRequested)
        {
            await ReleaseLeaseAsync(job, lease).ConfigureAwait(false);
            if (latest is { CancelRequested: true } && BackupJobStates.IsRunnable(latest.State))
            {
                Finish(latest, BackupJobStates.Cancelled, BackupReasonCodes.Cancelled, "Cancelled before start.", CancelledEvent(latest), null);
            }

            return;
        }

        jobs.SetLease(job.JobId, lease);
        job = events.Transition(job.JobId, new JobTransition
        {
            State = InitialRunningState(job.Kind),
            MarkStarted = true,
            Message = job.Attempt > 0 ? $"Attempt {job.Attempt + 1} started." : "Started."
        }, StartedEvent(job));

        // ---------------------------------------------------------------- 2. grant
        var grantResult = await controlPlane.RequestGrantAsync(new StorageGrantRequest
        {
            RepositoryId = context.RepositoryId,
            Operation = job.Kind,
            JobId = job.JobId,
            LeaseId = lease.LeaseId
        }, jobCancellation).ConfigureAwait(false);

        var grantError = ValidateGrant(grantResult, context);
        if (grantError is not null)
        {
            await ReleaseLeaseAsync(job, lease).ConfigureAwait(false);
            if (grantResult.Status == CallStatus.Transient)
            {
                RetryOrFail(job, BackupReasonCodes.StorageGrantFailed, grantError, transientOverride: true);
            }
            else
            {
                Finish(job, BackupJobStates.Failed, grantError.StartsWith("Unsupported", StringComparison.Ordinal) ? BackupReasonCodes.UnsupportedProvider : BackupReasonCodes.StorageGrantFailed,
                    grantError, FailedEvent(job), null);
            }

            return;
        }

        var grant = grantResult.Value!;

        // ---------------------------------------------------------------- 3. budget + worker
        var stopReasonHolder = new StopReason();
        var budget = governor.ResolveBudget(context.Profile, lease);
        var spec = BuildSpec(job, context, grant, budget);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(jobCancellation);
        using var maxDuration = new CancellationTokenSource(TimeSpan.FromHours(Math.Clamp(_options.MaxJobHours, 1, 168)));
        using var maxDurationRegistration = maxDuration.Token.Register(() =>
        {
            stopReasonHolder.Code ??= BackupReasonCodes.JobTimeout;
            stopReasonHolder.Message ??= $"The job exceeded the maximum duration of {_options.MaxJobHours} h and was stopped.";
            runCts.Cancel();
        });
        var stopReason = stopReasonHolder;
        var watchTask = WatchLeaseAndCancellationAsync(job, lease, context.OfflineGrace, stopReason, runCts);

        var callbacks = new RunnerCallbacks(this, job, context, grant, lease);
        WorkerResult result;
        try
        {
            result = await workerHost.RunAsync(
                spec, budget, context.VolumeRoots, job.Kind == BackupJobKinds.Maintenance, callbacks,
                sample =>
                {
                    callbacks.LatestSample = sample;
                    return Task.CompletedTask;
                },
                runCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await runCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        job = jobs.Get(job.JobId) ?? job;

        if (_serviceStopping.IsCancellationRequested && stopReason.Code != BackupReasonCodes.Cancelled)
        {
            logger.LogWarning("Agent stopping: job {JobId} left in {State} for reconciliation on restart.", job.JobId, job.State);
            return;
        }

        // ---------------------------------------------------------------- 4. outcome
        if (stopReason.Code is not null && result.State is BackupJobStates.Cancelled or BackupJobStates.Failed)
        {
            if (stopReason.Code == BackupReasonCodes.Cancelled)
            {
                Finish(job, BackupJobStates.Cancelled, BackupReasonCodes.Cancelled, "Cancelled.", CancelledEvent(job), result.Summary);
            }
            else
            {
                RetryOrFail(job, stopReason.Code, stopReason.Message ?? "Stopped.");
            }
        }
        else
        {
            switch (result.State)
            {
                case BackupJobStates.Succeeded:
                case BackupJobStates.Partial:
                    OnSuccess(job, context, result);
                    break;
                case BackupJobStates.Cancelled:
                    Finish(job, BackupJobStates.Cancelled, BackupReasonCodes.Cancelled, result.Message ?? "Cancelled.", CancelledEvent(job), result.Summary);
                    break;
                // A worker stopped by a cancel request reports Failed/CANCELLED: that is a user cancel
                // (state CANCELLED, never retried), not a failure.
                case BackupJobStates.Failed when result.ReasonCode == BackupReasonCodes.Cancelled || jobs.Get(job.JobId)?.CancelRequested == true:
                    Finish(job, BackupJobStates.Cancelled, BackupReasonCodes.Cancelled, "Cancelled.", CancelledEvent(job), result.Summary);
                    break;
                default:
                    RetryOrFail(job, result.ReasonCode ?? BackupReasonCodes.EngineFailed, result.Message ?? "Job failed.", summary: result.Summary);
                    break;
            }
        }

        await ReleaseLeaseAsync(job, lease).ConfigureAwait(false);
    }

    // ==================================================================== context

    private sealed record RunContext(
        string RepositoryId,
        string Engine,
        string Profile,
        long EstimatedBytes,
        IReadOnlyCollection<string> VolumeRoots,
        BackupPolicyDocument? Policy,
        JobSpecData Spec,
        TimeSpan OfflineGrace,
        string? Error = null,
        string ErrorCode = BackupReasonCodes.PolicyInvalid);

    private RunContext ResolveContext(BackupJobRecord job)
    {
        var spec = JobSpecData.FromJson(job.SpecJson);
        switch (job.Kind)
        {
            case BackupJobKinds.Backup:
            {
                var policy = job.PolicyId is null ? null : policies.Get(job.PolicyId);
                if (policy is null)
                {
                    return Error("The backup policy for this job is no longer applied.", BackupReasonCodes.PolicyNotFound);
                }

                if (!policy.Enabled)
                {
                    return Error("The backup policy is disabled.", BackupReasonCodes.PolicyNotFound);
                }

                var estimate = policies.GetScheduleState(policy.PolicyId).LastSourceBytes ?? 0;
                var volumes = policy.Sources.Select(s => Path.GetPathRoot(s.Path)!).Where(r => !string.IsNullOrEmpty(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // A per-run profile (on-demand "Back up now") wins over the policy's scheduled profile.
                return new RunContext(policy.Repository.RepositoryId, policy.Repository.Engine, spec.ResourceProfile ?? policy.ResourceProfile, estimate, volumes, policy, spec,
                    TimeSpan.FromMinutes(policy.OfflineGraceMinutes));
            }

            case BackupJobKinds.Restore when spec.Restore is { } r && r.RepositoryId is not null && r.Engine is not null:
            {
                var target = r.TargetKind == RestoreTargetKinds.Original ? r.SourceRoot : r.TargetPath;
                var root = target is null ? null : Path.GetPathRoot(target);
                // Restores are user-initiated and time-sensitive → Balanced, still governed (§7).
                return new RunContext(r.RepositoryId, r.Engine, spec.ResourceProfile ?? ResourceProfileNames.Balanced, 0,
                    root is null ? [] : [root], null, spec, TimeSpan.Zero);
            }

            case BackupJobKinds.Verify when spec.Verify is { } v && v.RepositoryId is not null && v.Engine is not null:
                return new RunContext(v.RepositoryId, v.Engine, spec.ResourceProfile ?? ResourceProfileNames.Eco, 0, [], null, spec, TimeSpan.Zero);

            // §7 "Maintenance throttled and isolated from active application and backups" → Eco.
            case BackupJobKinds.Maintenance when spec.Maintenance is { } m && m.RepositoryId is not null && m.Engine is not null:
            {
                var policy = m.PolicyId is null ? null : policies.Get(m.PolicyId);
                return new RunContext(m.RepositoryId, m.Engine, ResourceProfileNames.Eco, 0, [], policy, spec, TimeSpan.Zero);
            }

            default:
                return Error("The job parameters are incomplete.", BackupReasonCodes.PolicyInvalid);
        }

        static RunContext Error(string message, string code) =>
            new("", "", ResourceProfileNames.Eco, 0, [], null, new JobSpecData(), TimeSpan.Zero, message, code);
    }

    private static string? ValidateGrant(CallResult<StorageGrant> result, RunContext context)
    {
        if (result.Status != CallStatus.Ok || result.Value is null)
        {
            return $"Storage grant unavailable ({result.Detail}).";
        }

        var grant = result.Value;
        if (!string.Equals(grant.Repository.RepositoryId, context.RepositoryId, StringComparison.Ordinal))
        {
            return "Storage grant is for a different repository.";
        }

        if (!string.Equals(grant.Repository.Engine, context.Engine, StringComparison.OrdinalIgnoreCase))
        {
            return "Storage grant engine does not match the repository engine.";
        }

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return "Storage grant is already expired.";
        }

        return grant.Provider switch
        {
            StorageProviderTypes.GoogleDrive when grant.GoogleDrive is null => "Storage grant is missing Google Drive access.",
            StorageProviderTypes.S3 when grant.S3 is null => "Storage grant is missing S3 access.",
            StorageProviderTypes.GoogleDrive or StorageProviderTypes.S3 => null,
            _ => $"Unsupported storage provider '{grant.Provider}'."
        };
    }

    private WorkerJobSpec BuildSpec(BackupJobRecord job, RunContext context, StorageGrant grant, ResourceBudget budget)
    {
        var cacheDir = paths.CacheFor(context.RepositoryId);
        Directory.CreateDirectory(cacheDir);
        return new WorkerJobSpec
        {
            JobId = job.JobId,
            Kind = job.Kind,
            CorrelationId = job.CorrelationId,
            EndpointId = identity.AgentId,
            Engine = context.Engine,
            Grant = grant,
            PolicyVersionId = job.PolicyVersionId,
            Paths = new WorkerPaths
            {
                EngineDirectory = paths.EngineDirectory,
                CacheDirectory = cacheDir,
                WorkDirectory = paths.WorkFor(job.JobId),
                ManifestPath = job.Kind == BackupJobKinds.Backup ? paths.ManifestFor(job.JobId) : null
            },
            Budget = new WorkerBudget
            {
                Profile = budget.Profile,
                Parallelism = budget.Parallelism,
                MaxUploadBytesPerSecond = budget.MaxUploadBytesPerSecond,
                GoMaxProcs = budget.GoMaxProcs,
                GoMemoryLimitMb = budget.GoMemoryLimitMb,
                ContentCacheMb = Math.Min(budget.CacheMb, _options.MaxEngineCacheMb / 2),
                MetadataCacheMb = Math.Min(budget.CacheMb, _options.MaxEngineCacheMb / 2)
            },
            Sources = context.Policy?.Sources ?? [],
            Retention = context.Policy?.Retention,
            Consistency = context.Policy?.Consistency,
            Verification = context.Policy?.Verification,
            EngineSnapshotId = context.Spec.Verify?.EngineSnapshotId,
            VerifyFilesPercent = context.Spec.Verify?.VerifyFilesPercent,
            Restore = context.Spec.Restore,
            CompletedSnapshots = ReadCompletedSnapshots(job.JobId),
            MaintenanceFull = context.Spec.Maintenance?.Full ?? true
        };
    }

    /// <summary>Snapshots already taken for this job in earlier attempts (§8 checkpoint metadata) - those sources are not re-run.</summary>
    private IReadOnlyList<SnapshotRef> ReadCompletedSnapshots(string jobId)
    {
        var checkpoint = jobs.GetCheckpoint(jobId);
        if (checkpoint?.EngineCheckpointRef is not { Length: > 0 } json)
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<SnapshotRef>>(json, Transport.BackendApiClient.SnakeCaseJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Deletes leftover per-job work folders (engine config) after a crash - called once at startup.</summary>
    public void CleanupStaleWorkDirectories()
    {
        try
        {
            if (Directory.Exists(paths.WorkRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(paths.WorkRoot))
                {
                    TryDeleteDirectory(dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not clean stale work directories.");
        }
    }

    // ==================================================================== lease + cancel watch

    private sealed class StopReason
    {
        public string? Code { get; set; }
        public string? Message { get; set; }
    }

    private async Task WatchLeaseAndCancellationAsync(BackupJobRecord job, AdmissionLease lease, TimeSpan offlineGrace, StopReason stop, CancellationTokenSource runCts)
    {
        var current = lease;
        var nextRenewal = NextRenewalTime(current);
        var token = runCts.Token;

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), time, token).ConfigureAwait(false);

            if (jobs.Get(job.JobId)?.CancelRequested == true)
            {
                stop.Code = BackupReasonCodes.Cancelled;
                await runCts.CancelAsync().ConfigureAwait(false);
                return;
            }

            var now = time.GetUtcNow();
            if (now < nextRenewal)
            {
                continue;
            }

            var renewed = await controlPlane.RenewLeaseAsync(current.LeaseId,
                new LeaseRenewRequest { JobId = job.JobId, FencingToken = current.FencingToken }, token).ConfigureAwait(false);

            if (renewed is { Status: CallStatus.Ok, Value: { } newLease })
            {
                current = newLease;
                jobs.SetLease(job.JobId, newLease);
                nextRenewal = NextRenewalTime(newLease);
                continue;
            }

            if (renewed.Status == CallStatus.Rejected)
            {
                // The scheduler gave this capacity to someone else (or fenced us out) - stop now.
                stop.Code = BackupReasonCodes.LeaseLost;
                stop.Message = $"Admission lease was revoked ({renewed.Detail}).";
                await runCts.CancelAsync().ConfigureAwait(false);
                return;
            }

            // Transient: keep working while the lease (plus any approved offline grace, ADR-04) is still valid.
            if (now > current.ExpiresAt + offlineGrace)
            {
                stop.Code = BackupReasonCodes.LeaseLost;
                stop.Message = "Admission lease expired while the control plane was unreachable.";
                await runCts.CancelAsync().ConfigureAwait(false);
                return;
            }

            nextRenewal = now.AddSeconds(30);
        }
    }

    private DateTimeOffset NextRenewalTime(AdmissionLease lease)
    {
        var now = time.GetUtcNow();
        var ttl = lease.ExpiresAt - now;
        return now + TimeSpan.FromSeconds(Math.Max(15, ttl.TotalSeconds / 2));
    }

    private async Task ReleaseLeaseAsync(BackupJobRecord job, AdmissionLease lease)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await controlPlane.ReleaseLeaseAsync(lease.LeaseId, new LeaseRenewRequest { JobId = job.JobId, FencingToken = lease.FencingToken }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // Leases have a TTL; the backend reclaims it anyway.
        }
    }

    // ==================================================================== worker callbacks

    private sealed class RunnerCallbacks(BackupJobRunner runner, BackupJobRecord job, RunContext context, StorageGrant initialGrant, AdmissionLease lease) : IWorkerCallbacks
    {
        private string _stage = InitialRunningState(job.Kind);
        private string _grantId = initialGrant.GrantId;

        public ResourceSample? LatestSample { get; set; }

        public void OnStage(string stage)
        {
            if (stage == _stage || BackupJobStates.IsTerminal(stage) || !BackupJobStates.IsActive(stage))
            {
                return;
            }

            _stage = stage;
            runner.Events.Transition(job.JobId, new JobTransition { State = stage }, BackupEventTypes.StageChanged, resource: LatestSample);
            runner.Jobs.SaveCheckpoint(job.JobId, stage, null, null, null, runner.Time.GetUtcNow());
        }

        private readonly List<SnapshotRef> _completed = [.. runner.ReadCompletedSnapshots(job.JobId)];

        public void OnSnapshotCompleted(SnapshotRef snapshot)
        {
            _completed.RemoveAll(s => string.Equals(s.SourcePath, snapshot.SourcePath, StringComparison.OrdinalIgnoreCase));
            _completed.Add(snapshot);
            runner.Jobs.SaveCheckpoint(job.JobId, _stage,
                System.Text.Json.JsonSerializer.Serialize(_completed, Transport.BackendApiClient.SnakeCaseJson),
                snapshot.SnapshotId, null, runner.Time.GetUtcNow());
        }

        public void OnProgress(JobProgress? progress, double? uploadBytesPerSecond, long? scannerDurationMs)
        {
            if (runner.Events.Progress(job.JobId, _stage, progress, LatestSample) && progress is not null)
            {
                runner.Jobs.SaveCheckpoint(job.JobId, _stage, null, null,
                    System.Text.Json.JsonSerializer.Serialize(progress, Transport.BackendApiClient.SnakeCaseJson), runner.Time.GetUtcNow());
            }
        }

        public async Task<StorageGrant?> RenewGrantAsync(CancellationToken cancellationToken)
        {
            var result = await runner.ControlPlane.RequestGrantAsync(new StorageGrantRequest
            {
                RepositoryId = context.RepositoryId,
                Operation = job.Kind,
                JobId = job.JobId,
                LeaseId = lease.LeaseId,
                PreviousGrantId = _grantId
            }, cancellationToken).ConfigureAwait(false);

            if (ValidateGrant(result, context) is { } error)
            {
                runner.Logger.LogWarning("Storage grant renewal for job {JobId} failed: {Error}", job.JobId, error);
                return null;
            }

            _grantId = result.Value!.GrantId;
            return result.Value;
        }
    }

    // ==================================================================== outcomes

    private void OnSuccess(BackupJobRecord job, RunContext context, WorkerResult result)
    {
        var now = time.GetUtcNow();
        if (job.Kind == BackupJobKinds.Backup)
        {
            var manifest = paths.ManifestFor(job.JobId);
            if (File.Exists(manifest))
            {
                outbox.Enqueue(new OutboxItem
                {
                    EventId = $"manifest-{job.JobId}",
                    Kind = OutboxKinds.Manifest,
                    Route = Agent.Contracts.Backup.ControlPlaneRoutes.JobManifest(job.JobId),
                    FilePath = manifest,
                    ContentType = "application/x-ndjson",
                    JobId = job.JobId,
                    IsTerminal = true
                }, now);
            }

            var verified = result.Summary?.VerificationState == VerificationStates.Verified;
            policies.RecordSuccess(context.Policy!.PolicyId, now, verified, result.Summary?.SourceBytes);
        }

        var eventType = job.Kind switch
        {
            BackupJobKinds.Restore => BackupEventTypes.RestoreVerified,
            BackupJobKinds.Verify => BackupEventTypes.VerifyCompleted,
            BackupJobKinds.Maintenance => BackupEventTypes.MaintenanceCompleted,
            _ => result.Summary?.VerificationState == VerificationStates.Verified ? BackupEventTypes.Verified : BackupEventTypes.Succeeded
        };

        Finish(job, result.State, result.ReasonCode, result.Message ?? "Completed.", eventType, result.Summary);
        logger.LogInformation("Job {JobId} ({Kind}) finished {State}.", job.JobId, job.Kind, result.State);
    }

    private void RetryOrFail(BackupJobRecord job, string reasonCode, string message, bool transientOverride = false, JobResultSummary? summary = null)
    {
        var transient = transientOverride || BackupReasonCodes.IsTransient(reasonCode);
        var attemptsUsed = job.Attempt + 1;
        if (transient && attemptsUsed < job.MaxAttempts)
        {
            var delay = FullJitterBackoff(attemptsUsed);
            events.Transition(job.JobId, new JobTransition
            {
                State = BackupJobStates.Retrying,
                ReasonCode = reasonCode,
                Message = $"{message} Retrying in {Math.Ceiling(delay.TotalMinutes)} min (attempt {attemptsUsed + 1}/{job.MaxAttempts}).",
                NotBefore = time.GetUtcNow() + delay,
                IncrementAttempt = true,
                ClearLease = true
            }, BackupEventTypes.Retrying, summary);
            logger.LogWarning("Job {JobId} will retry ({Reason}): {Message}", job.JobId, reasonCode, message);
            return;
        }

        var finalMessage = transient ? $"{message} Retries exhausted after {attemptsUsed} attempt(s)." : message;
        Finish(job, BackupJobStates.Failed, reasonCode, finalMessage, FailedEvent(job), summary);
        logger.LogWarning("Job {JobId} failed ({Reason}): {Message}", job.JobId, reasonCode, finalMessage);
    }

    private void Finish(BackupJobRecord job, string state, string? reasonCode, string message, string eventType, JobResultSummary? summary) =>
        events.Transition(job.JobId, new JobTransition
        {
            State = state,
            ReasonCode = reasonCode,
            Message = message,
            MarkEnded = true,
            ClearLease = true
        }, eventType, summary);

    private void Defer(BackupJobRecord job, string reasonCode, string message, TimeSpan? retryAfter)
    {
        var baseDelay = retryAfter ?? TimeSpan.FromMinutes(_options.ControlPlaneUnreachableRetryMinutes);
        var jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Min(300, baseDelay.TotalSeconds));
        DeferUntil(job, reasonCode, message, time.GetUtcNow() + baseDelay + jitter);
    }

    private void DeferUntil(BackupJobRecord job, string reasonCode, string message, DateTimeOffset notBefore)
    {
        if (_serviceStopping.IsCancellationRequested)
        {
            return;
        }

        // Not an attempt: the job never started. State stays QUEUED/RETRYING.
        events.Transition(job.JobId, new JobTransition
        {
            State = BackupJobStates.IsRunnable(job.State) ? job.State : BackupJobStates.Queued,
            ReasonCode = reasonCode,
            Message = message,
            NotBefore = notBefore,
            ClearLease = true
        }, BackupEventTypes.Deferred);
        logger.LogInformation("Job {JobId} deferred until {NotBefore:O}: {Message}", job.JobId, notBefore, message);
    }

    private TimeSpan FullJitterBackoff(int attempt)
    {
        var cap = Math.Min(_options.RetryMaxMinutes * 60.0, _options.RetryBaseSeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        var floor = Math.Min(30, cap);
        return TimeSpan.FromSeconds(floor + (Random.Shared.NextDouble() * (cap - floor)));
    }

    private static string InitialRunningState(string kind) => kind switch
    {
        BackupJobKinds.Restore => BackupJobStates.Restoring,
        BackupJobKinds.Verify => BackupJobStates.Verifying,
        BackupJobKinds.Maintenance => BackupJobStates.Maintaining,
        _ => BackupJobStates.Snapshotting
    };

    private static string StartedEvent(BackupJobRecord job) =>
        job.Kind == BackupJobKinds.Restore ? BackupEventTypes.RestoreStarted : BackupEventTypes.Started;

    private static string FailedEvent(BackupJobRecord job) =>
        job.Kind == BackupJobKinds.Restore ? BackupEventTypes.RestoreFailed : BackupEventTypes.Failed;

    private static string CancelledEvent(BackupJobRecord job) =>
        job.Kind == BackupJobKinds.Restore ? BackupEventTypes.RestoreFailed : BackupEventTypes.Cancelled;

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete job work directory {Path}.", path);
        }
    }

    /// <summary>Startup reconciliation entry point (§15 "Agent/core crash during 10 GB upload → Job reconciles").</summary>
    public void ReconcileInterrupted(BackupJobRecord job) =>
        RetryOrFail(job, BackupReasonCodes.AgentRestarted, "The agent restarted while this job was running.");

    /// <summary>Called by the scheduler when a newer slot replaces an older queued run.</summary>
    public void Supersede(BackupJobRecord job) =>
        Finish(job, BackupJobStates.Cancelled, BackupReasonCodes.Superseded, "Replaced by a newer scheduled run.", CancelledEvent(job), null);
}
