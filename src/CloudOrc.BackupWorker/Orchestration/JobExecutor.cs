using System.Diagnostics;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker.Consistency;
using CloudOrc.BackupWorker.Engines;
using CloudOrc.BackupWorker.Restore;
using CloudOrc.BackupWorker.Scanning;
using CloudOrc.BackupWorker.Storage;

namespace CloudOrc.BackupWorker.Orchestration;

/// <summary>
/// Runs one job inside the worker process and returns its terminal result. Stages follow the
/// guide's lifecycle: SNAPSHOTTING (consistency plan) → SCANNING (size + manifest) →
/// TRANSFERRING (engine snapshot, VSS held by the engine) → VERIFYING (engine verify).
/// It never claims more than it proved: read errors, degraded consistency or a failed
/// verification produce PARTIAL with a reason code, never a silent SUCCEEDED (§6, §15).
/// </summary>
public static class JobExecutor
{
    private static readonly TimeSpan GrantRenewLead = TimeSpan.FromMinutes(10);

    public static async Task<WorkerResult> RunAsync(WorkerJobSpec spec, IWorkerReporter reporter, CancellationToken ct)
    {
        Directory.CreateDirectory(spec.Paths.WorkDirectory);
        Directory.CreateDirectory(spec.Paths.CacheDirectory);

        EngineBinary engineBinary;
        EngineBinary? rclone = null;
        try
        {
            engineBinary = EngineBinaryVerifier.Resolve(spec.Paths.EngineDirectory, spec.Engine);
            if (StorageTarget.NeedsRclone(reporter.CurrentGrant))
            {
                rclone = EngineBinaryVerifier.Resolve(spec.Paths.EngineDirectory, "rclone");
            }
        }
        catch (EngineBinaryException ex)
        {
            return Failed(ex.ReasonCode, ex.Message);
        }

        IBackupEngine engine = spec.Engine switch
        {
            BackupEngineNames.Kopia => new KopiaEngine(),
            BackupEngineNames.Restic => new ResticEngine(),
            _ => throw new InvalidOperationException($"Unknown engine '{spec.Engine}'.")
        };

        await using var broker = reporter.CurrentGrant.Provider == StorageProviderTypes.GoogleDrive
            ? new GoogleDriveTokenBroker(token => FreshDriveTokenAsync(reporter, token))
            : null;

        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewLoop = KeepGrantFreshAsync(reporter, renewCts.Token);

        var grant = reporter.CurrentGrant;
        var context = new EngineContext
        {
            Engine = engineBinary,
            Storage = () => StorageTarget.Build(reporter.CurrentGrant, spec.Paths.WorkDirectory, rclone, broker),
            CurrentGrantId = () => reporter.CurrentGrant.GrantId,
            WorkDirectory = spec.Paths.WorkDirectory,
            CacheDirectory = spec.Paths.CacheDirectory,
            Budget = spec.Budget,
            SnapshotHost = grant.Repository.SnapshotHost ?? spec.EndpointId,
            SnapshotUser = grant.Repository.SnapshotUser ?? "cloudorc",
            JobId = spec.JobId,
            Password = grant.Repository.Password,
            CreateIfMissing = grant.Repository.CreateIfMissing && spec.Kind == BackupJobKinds.Backup,
            Log = reporter.Log
        };

        try
        {
            return spec.Kind switch
            {
                BackupJobKinds.Backup => await BackupAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                BackupJobKinds.Verify => await VerifyAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                BackupJobKinds.Restore => await RestoreAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                BackupJobKinds.Maintenance => await MaintenanceAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                _ => Failed(BackupReasonCodes.PolicyInvalid, $"Unknown job kind '{spec.Kind}'.")
            };
        }
        finally
        {
            await engine.DisconnectAsync(context).ConfigureAwait(false);
            await renewCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // ==================================================================== backup

    private static async Task<WorkerResult> BackupAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var consistencyPolicy = spec.Consistency ?? new BackupConsistencyPolicy();
        var retention = spec.Retention ?? new BackupRetention();

        // ---- SNAPSHOTTING: decide what consistency we can honestly deliver.
        reporter.Stage(BackupJobStates.Snapshotting);
        await ConsistencyPlanner.WaitForApplicationCloseAsync(spec.Sources, consistencyPolicy, ConsistencyPlanner.AnyProcessRunning,
            TimeSpan.FromSeconds(30), message => reporter.Log("warning", message), ct).ConfigureAwait(false);
        var plan = ConsistencyPlanner.Plan(spec.Sources, consistencyPolicy, ConsistencyPlanner.ReadVolume, ConsistencyPlanner.AnyProcessRunning);
        foreach (var note in plan.Notes)
        {
            reporter.Log("warning", note);
        }

        if (plan.FailReasonCode is not null)
        {
            return Failed(plan.FailReasonCode, plan.FailMessage ?? "Consistency requirements not met.");
        }

        // ---- SCANNING: size the work and write the browse manifest.
        reporter.Stage(BackupJobStates.Scanning);
        var scan = SourceScanner.Scan(spec.Sources, spec.Paths.ManifestPath, spec.Budget.MaxManifestEntries,
            (files, bytes) => reporter.Progress(new JobProgress { FilesTotal = files, BytesTotal = bytes }), ct);
        reporter.Progress(new JobProgress { FilesTotal = scan.Files, BytesTotal = scan.Bytes, Errors = scan.Errors }, scannerDurationMs: scan.DurationMs);

        var present = spec.Sources.Where(s => !scan.MissingSources.Contains(s.Path)).ToList();
        if (present.Count == 0)
        {
            return Failed(BackupReasonCodes.SourceNotFound, "None of the policy's source paths exist on this device.");
        }

        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        // ---- TRANSFERRING: one engine snapshot per source.
        reporter.Stage(BackupJobStates.Transferring);
        var snapshots = new List<SnapshotRef>();

        // Sources finished by an earlier attempt of this job keep their snapshot (§8 checkpoints).
        foreach (var done in spec.CompletedSnapshots.Where(c => present.Any(p => string.Equals(p.Path, c.SourcePath, StringComparison.OrdinalIgnoreCase))))
        {
            snapshots.Add(done);
            reporter.Log("info", $"Source {done.SourcePath} was already backed up by an earlier attempt; skipping it.");
        }
        long filesNew = 0, filesChanged = 0, filesUnchanged = 0, uploaded = 0, errors = scan.Errors;
        EngineOutcome? firstFailure = null;
        var options = new BackupRunOptions(plan.UseVss, consistencyPolicy.VssMode, plan.UseVss ? plan.SnapshotLifetime : null, [], spec.FullScan);

        foreach (var source in present)
        {
            ct.ThrowIfCancellationRequested();
            if (snapshots.Any(s => string.Equals(s.SourcePath, source.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var perSource = options with { Excludes = source.Excludes };

            var prepared = await engine.PrepareSourceAsync(ctx, source, retention, perSource, ct).ConfigureAwait(false);
            if (!prepared.Ok)
            {
                firstFailure ??= prepared;
                break;
            }

            var outcome = await engine.BackupAsync(ctx, source, perSource,
                (progress, rate) => reporter.Progress(new JobProgress
                {
                    FilesTotal = progress.FilesTotal ?? scan.Files,
                    FilesDone = progress.FilesDone,
                    BytesTotal = progress.BytesTotal ?? scan.Bytes,
                    BytesRead = progress.BytesRead,
                    BytesUploaded = progress.BytesUploaded,
                    Percent = progress.Percent,
                    EtaSeconds = progress.EtaSeconds,
                    Errors = progress.Errors
                }, rate), ct).ConfigureAwait(false);

            if (!outcome.Ok)
            {
                firstFailure ??= outcome;
                if (outcome.ReasonCode == BackupReasonCodes.Cancelled)
                {
                    return new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled, Message = "Cancelled." };
                }

                break;
            }

            var snapshotRef = new SnapshotRef
            {
                SourcePath = source.Path,
                SnapshotId = outcome.SnapshotId!,
                RootObjectId = outcome.RootObjectId,
                Files = outcome.Files,
                Bytes = outcome.Bytes,
                Errors = outcome.Errors
            };
            snapshots.Add(snapshotRef);
            reporter.SnapshotCompleted(snapshotRef);
            filesNew += outcome.FilesNew ?? 0;
            filesChanged += outcome.FilesChanged ?? 0;
            filesUnchanged += outcome.FilesUnchanged ?? 0;
            uploaded += outcome.BytesUploaded ?? 0;
            errors += outcome.Errors ?? 0;

            var retained = await engine.ApplyRetentionAsync(ctx, source, retention, ct).ConfigureAwait(false);
            if (!retained.Ok)
            {
                reporter.Log("warning", $"Retention for {source.Path} could not be applied now ({retained.ReasonCode}); maintenance will retry.");
            }
        }

        if (snapshots.Count == 0)
        {
            return Failed(firstFailure?.ReasonCode ?? BackupReasonCodes.EngineFailed, firstFailure?.Message ?? "No snapshot was created.");
        }

        // ---- VERIFYING: engine-native verification of what was just written.
        reporter.Stage(BackupJobStates.Verifying);
        var percent = Math.Clamp(spec.Verification?.VerifyFilesPercent ?? 0, 0, 100);
        var verify = await engine.VerifyAsync(ctx, snapshots.Select(s => s.SnapshotId).ToList(), percent, ct).ConfigureAwait(false);
        var verification = verify.Ok ? VerificationStates.Verified : VerificationStates.VerificationFailed;

        // Tally may have been started during the run: re-check before claiming consistency.
        var consistency = plan.ExpectedLevel;
        if (consistency == ConsistencyLevels.ApplicationConsistent && spec.Sources.Any(s => s.Kind == BackupSourceKinds.Tally)
            && ConsistencyPlanner.AnyProcessRunning(consistencyPolicy.Tally.ProcessNames))
        {
            consistency = ConsistencyLevels.CrashConsistent;
        }

        var summary = new JobResultSummary
        {
            Engine = engine.Name,
            EngineVersion = ctx.Engine.Version,
            EngineSnapshots = snapshots,
            Consistency = consistency,
            VerificationState = verification,
            VerifyFilesPercent = percent,
            SourceTime = started,
            FilesNew = filesNew,
            FilesChanged = filesChanged,
            FilesUnchanged = filesUnchanged,
            SourceBytes = scan.Bytes,
            BackupMode = spec.FullScan ? BackupModes.Full : BackupModes.Incremental,
            BytesRead = scan.Bytes,
            BytesUploaded = uploaded,
            Errors = errors,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ManifestEntries = scan.ManifestEntries
        };

        // ---- Outcome: never claim more than was proven.
        var (state, reason, message) = (BackupJobStates.Succeeded, (string?)null, "Backup completed and verified.");
        if (firstFailure is not null)
        {
            (state, reason, message) = (BackupJobStates.Partial, firstFailure.ReasonCode, $"Only {snapshots.Count} of {present.Count} sources were backed up: {firstFailure.Message}");
        }
        else if (!verify.Ok)
        {
            (state, reason, message) = (BackupJobStates.Partial, BackupReasonCodes.VerificationFailed, $"Backup written but verification failed: {verify.Message}");
        }
        else if (scan.MissingSources.Count > 0)
        {
            (state, reason, message) = (BackupJobStates.Partial, BackupReasonCodes.SourceNotFound, $"{scan.MissingSources.Count} source path(s) no longer exist.");
        }
        else if (errors > 0)
        {
            (state, reason, message) = (BackupJobStates.Partial, BackupReasonCodes.SourceReadErrors, $"{errors} file(s) could not be read.");
        }
        else if (ConsistencyPlanner.IsDegraded(consistency, consistencyPolicy))
        {
            (state, reason, message) = (BackupJobStates.Partial,
                consistency == ConsistencyLevels.CrashConsistent ? BackupReasonCodes.ApplicationNotQuiesced : BackupReasonCodes.SnapshotFailed,
                consistency == ConsistencyLevels.CrashConsistent ? "Backup completed while Tally was running (crash-consistent)." : "Backup completed without a snapshot (files read live).");
        }

        return new WorkerResult { State = state, ReasonCode = reason, Message = message, Summary = summary };
    }

    // ==================================================================== verify

    private static async Task<WorkerResult> VerifyAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.EngineSnapshotId))
        {
            return Failed(BackupReasonCodes.PolicyInvalid, "No recovery point to verify.");
        }

        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        reporter.Stage(BackupJobStates.Verifying);
        var percent = Math.Clamp(spec.VerifyFilesPercent ?? 5, 0, 100);
        var verify = await engine.VerifyAsync(ctx, [spec.EngineSnapshotId], percent, ct).ConfigureAwait(false);
        var summary = new JobResultSummary
        {
            Engine = engine.Name,
            EngineVersion = ctx.Engine.Version,
            EngineSnapshots = [new SnapshotRef { SourcePath = "", SnapshotId = spec.EngineSnapshotId }],
            VerificationState = verify.Ok ? VerificationStates.Verified : VerificationStates.VerificationFailed,
            VerifyFilesPercent = percent
        };

        return verify.Ok
            ? new WorkerResult { State = BackupJobStates.Succeeded, Message = "Recovery point verified.", Summary = summary }
            : new WorkerResult { State = BackupJobStates.Failed, ReasonCode = verify.ReasonCode ?? BackupReasonCodes.VerificationFailed, Message = verify.Message, Summary = summary };
    }

    // ==================================================================== restore

    private static async Task<WorkerResult> RestoreAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        var restore = spec.Restore;
        if (restore?.EngineSnapshotId is null || restore.SourceRoot is null)
        {
            return Failed(BackupReasonCodes.RestoreTargetInvalid, "Restore parameters are incomplete.");
        }

        RestorePlan plan;
        try
        {
            plan = RestorePathGuard.Plan(restore, DateTimeOffset.UtcNow);
        }
        catch (RestoreRejectedException ex)
        {
            return Failed(BackupReasonCodes.RestorePathUnsafe, ex.Message);
        }

        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        reporter.Stage(BackupJobStates.Restoring);
        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(plan.TargetDirectory);
        var done = 0;
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await engine.RestoreAsync(ctx, restore.EngineSnapshotId, restore.SourceRoot, item, plan.TargetDirectory, restore.Mode, ct).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                return outcome.ReasonCode == BackupReasonCodes.Cancelled
                    ? new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled, Message = "Cancelled." }
                    : Failed(outcome.ReasonCode ?? BackupReasonCodes.EngineFailed, outcome.Message ?? "Restore failed.");
            }

            done++;
            reporter.Progress(new JobProgress { FilesDone = done, FilesTotal = plan.Items.Count, Percent = Math.Round(done * 100.0 / plan.Items.Count, 1) });
        }

        // ---- VERIFYING: every selected item exists, nothing links outside the target.
        reporter.Stage(BackupJobStates.Verifying);
        long files = 0, bytes = 0;
        var unsafeLinks = 0;
        foreach (var item in plan.Items)
        {
            var destination = item is null ? plan.TargetDirectory : Path.Combine(plan.TargetDirectory, item);
            if (!File.Exists(destination) && !Directory.Exists(destination))
            {
                return Failed(BackupReasonCodes.RestoreVerificationFailed, "A selected item is missing after the restore.");
            }

            if (File.Exists(destination))
            {
                files++;
                bytes += new FileInfo(destination).Length;
                continue;
            }

            var audit = RestorePathGuard.Audit(destination);
            files += audit.Files;
            bytes += audit.Bytes;
            unsafeLinks += audit.UnsafeLinks;
        }

        var summary = new JobResultSummary
        {
            Engine = engine.Name,
            EngineVersion = ctx.Engine.Version,
            EngineSnapshots = [new SnapshotRef { SourcePath = restore.SourceRoot, SnapshotId = restore.EngineSnapshotId }],
            VerificationState = unsafeLinks == 0 ? VerificationStates.Verified : VerificationStates.VerificationFailed,
            RestoredFiles = files,
            RestoredBytes = bytes,
            RestoreTarget = plan.TargetDirectory,
            DurationMs = stopwatch.ElapsedMilliseconds
        };

        return unsafeLinks == 0
            ? new WorkerResult { State = BackupJobStates.Succeeded, Message = $"Restored {files} file(s).", Summary = summary }
            : new WorkerResult
            {
                State = BackupJobStates.Partial,
                ReasonCode = BackupReasonCodes.RestorePathUnsafe,
                Message = $"Restore finished but {unsafeLinks} link(s) point outside the restore folder - review before use.",
                Summary = summary
            };
    }

    // ==================================================================== maintenance

    private static async Task<WorkerResult> MaintenanceAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        reporter.Stage(BackupJobStates.Maintaining);
        var stopwatch = Stopwatch.StartNew();
        var outcome = await engine.MaintenanceAsync(ctx, spec.MaintenanceFull, spec.Retention, ct).ConfigureAwait(false);
        var summary = new JobResultSummary { Engine = engine.Name, EngineVersion = ctx.Engine.Version, DurationMs = stopwatch.ElapsedMilliseconds };
        return outcome.Ok
            ? new WorkerResult { State = BackupJobStates.Succeeded, Message = "Maintenance completed.", Summary = summary }
            : Failed(outcome.ReasonCode ?? BackupReasonCodes.EngineFailed, outcome.Message ?? "Maintenance failed.");
    }

    // ==================================================================== grants

    private static async Task<GoogleDriveGrant?> FreshDriveTokenAsync(IWorkerReporter reporter, CancellationToken ct)
    {
        var grant = reporter.CurrentGrant;
        if (grant.GoogleDrive is { } drive && drive.TokenExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5))
        {
            return drive;
        }

        var renewed = await reporter.RenewGrantAsync(ct).ConfigureAwait(false);
        return renewed?.GoogleDrive;
    }

    /// <summary>Asks for a new grant ahead of expiry so a long job never runs on a dead credential.</summary>
    private static async Task KeepGrantFreshAsync(IWorkerReporter reporter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            var grant = reporter.CurrentGrant;
            var expiry = grant.GoogleDrive is { } d && d.TokenExpiresAt < grant.ExpiresAt ? d.TokenExpiresAt : grant.ExpiresAt;
            if (expiry - DateTimeOffset.UtcNow < GrantRenewLead)
            {
                if (await reporter.RenewGrantAsync(ct).ConfigureAwait(false) is null)
                {
                    reporter.Log("warning", "Storage grant renewal failed; the job will stop if the current grant expires.");
                }
            }
        }
    }

    private static WorkerResult Failed(string reasonCode, string message) =>
        new() { State = BackupJobStates.Failed, ReasonCode = reasonCode, Message = message };
}
