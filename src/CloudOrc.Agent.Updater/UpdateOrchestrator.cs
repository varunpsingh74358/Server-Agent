namespace CloudOrc.Agent.Updater;

/// <summary>
/// The update procedure, written purely against interfaces so every path is unit-testable:
/// verify (hash + Authenticode, fail closed) → record fromVersion → back up install dir →
/// run installer → health check (services running, exact version, health pipe HEALTHY) →
/// on any failure after the backup: stop services, restore backup exactly, start services,
/// health-check the old version.
/// </summary>
public sealed class UpdateOrchestrator(
    IFileHashVerifier hashVerifier,
    IAuthenticodeVerifier authenticodeVerifier,
    IVersionProbe versionProbe,
    IHealthProbe healthProbe,
    IServiceControl serviceControl,
    IInstallerRunner installerRunner,
    IDirectoryBackup directoryBackup,
    IUpdateLog log,
    TimeProvider timeProvider,
    TimeSpan? pollInterval = null,
    TimeSpan? installerTimeout = null)
{
    public static readonly string[] ServiceNames = ["CloudOrcControlAgent", "CloudOrcWatchdogAgent"];

    private const string HealthyStatus = "HEALTHY";
    private static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _installerTimeout = installerTimeout ?? TimeSpan.FromMinutes(15);

    public async Task<UpdateResult> RunAsync(UpdaterArguments args, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        log.Info($"Update {args.UpdateId} starting: installer '{args.InstallerPath}', expected version {args.ExpectedVersion}, install dir '{args.InstallDirectory}'.");

        // 1. Verification - fail closed, nothing is touched before this passes.
        var hash = hashVerifier.Verify(args.InstallerPath, args.Sha256);
        if (!hash.IsValid)
        {
            log.Error($"Hash verification failed: {hash.Reason}");
            return Result(args, startedAt, UpdateOutcomes.VerificationFailed, hash.Reason, fromVersion: null, installed: null, exitCode: null);
        }

        log.Info("SHA-256 verified.");

        if (args.AllowUnsigned)
        {
            log.Warn("!!! --allow-unsigned is set: Authenticode verification SKIPPED. Development use only - never in production. !!!");
        }
        else
        {
            var signature = authenticodeVerifier.Verify(args.InstallerPath);
            if (!signature.IsValid)
            {
                log.Error($"Signature verification failed: {signature.Reason}");
                return Result(args, startedAt, UpdateOutcomes.VerificationFailed, signature.Reason, fromVersion: null, installed: null, exitCode: null);
            }

            log.Info("Authenticode signature verified.");
        }

        // 2. Record the version we may need to roll back to.
        var fromVersion = await versionProbe.GetInstalledVersionAsync(cancellationToken).ConfigureAwait(false);
        log.Info($"Currently installed version: {fromVersion ?? "(unknown)"}.");

        // 3. Back up the whole install directory.
        try
        {
            directoryBackup.Backup(args.InstallDirectory, args.RollbackDirectory);
            log.Info($"Install directory backed up to '{args.RollbackDirectory}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            log.Error($"Backup failed, aborting before install: {ex.Message}");
            TryDeleteBackup(args);
            return Result(args, startedAt, UpdateOutcomes.FailedBeforeInstall, $"Backup of install directory failed: {ex.Message}", fromVersion, fromVersion, exitCode: null);
        }

        // 4. Install.
        int? exitCode;
        try
        {
            log.Info("Running installer silently.");
            exitCode = await installerRunner.RunAsync(args.InstallerPath, args.InstallerLogFile, _installerTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error($"Installer run threw: {ex.Message}");
            exitCode = null;
        }

        string failureReason;
        if (exitCode == 0)
        {
            log.Info("Installer exited 0. Checking health of the new version.");
            var health = await WaitForHealthyAsync(args.ExpectedVersion, args.HealthTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (health.Healthy)
            {
                log.Info($"Update {args.UpdateId} succeeded: {fromVersion ?? "(unknown)"} -> {health.Version}.");
                TryDeleteBackup(args);
                return Result(args, startedAt, UpdateOutcomes.Succeeded, null, fromVersion, health.Version, exitCode);
            }

            failureReason = $"Health check failed after install: {health.Detail}";
        }
        else
        {
            failureReason = exitCode is null
                ? "Installer did not complete (timed out, could not start, or crashed)."
                : exitCode == 30
                    ? "Installer refused the update (exit 30: downgrade blocked)."
                    : $"Installer exited with code {exitCode}.";
        }

        log.Error(failureReason + " Rolling back.");

        // 5. Rollback.
        var rollbackOk = await RollbackAsync(args, fromVersion, cancellationToken).ConfigureAwait(false);
        var installedNow = await SafeVersionAsync(cancellationToken).ConfigureAwait(false);
        if (rollbackOk.Healthy)
        {
            log.Info($"Rollback succeeded; running version {installedNow ?? "(unknown)"}.");
            return Result(args, startedAt, UpdateOutcomes.FailedRolledBack, failureReason, fromVersion, installedNow, exitCode);
        }

        log.Error($"ROLLBACK FAILED: {rollbackOk.Detail}. Backup kept at '{args.RollbackDirectory}' for manual recovery.");
        return Result(args, startedAt, UpdateOutcomes.RollbackFailed, $"{failureReason} Rollback failed: {rollbackOk.Detail}", fromVersion, installedNow, exitCode);
    }

    private async Task<HealthOutcome> RollbackAsync(UpdaterArguments args, string? fromVersion, CancellationToken cancellationToken)
    {
        foreach (var service in ServiceNames)
        {
            if (!serviceControl.Stop(service, ServiceStopTimeout))
            {
                log.Warn($"Service {service} did not stop cleanly; attempting restore anyway.");
            }
        }

        try
        {
            directoryBackup.Restore(args.RollbackDirectory, args.InstallDirectory);
            log.Info("Install directory restored from backup.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return new HealthOutcome(false, null, $"restore from backup failed: {ex.Message}");
        }

        foreach (var service in ServiceNames)
        {
            if (!serviceControl.Start(service, ServiceStartTimeout))
            {
                log.Warn($"Service {service} did not start after restore.");
            }
        }

        return await WaitForHealthyAsync(fromVersion, args.HealthTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polls until both services run, the version matches (if known) and the health pipe says HEALTHY.</summary>
    private async Task<HealthOutcome> WaitForHealthyAsync(string? expectedVersion, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow().AddSeconds(timeoutSeconds);
        var detail = "not checked";
        string? version = null;

        while (true)
        {
            var stopped = ServiceNames.Where(s => !serviceControl.IsRunning(s)).ToList();
            if (stopped.Count > 0)
            {
                detail = $"service(s) not running: {string.Join(", ", stopped)}";
            }
            else
            {
                version = await SafeVersionAsync(cancellationToken).ConfigureAwait(false);
                if (expectedVersion is not null && !string.Equals(version, expectedVersion, StringComparison.Ordinal))
                {
                    detail = $"installed version is {version ?? "(unknown)"}, expected {expectedVersion}";
                }
                else
                {
                    var status = await healthProbe.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    if (string.Equals(status, HealthyStatus, StringComparison.Ordinal))
                    {
                        return new HealthOutcome(true, version, "healthy");
                    }

                    detail = $"health pipe reports {status ?? "(no answer)"}";
                }
            }

            if (timeProvider.GetUtcNow() >= deadline)
            {
                return new HealthOutcome(false, version, detail + $" (after {timeoutSeconds}s)");
            }

            await Task.Delay(_pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> SafeVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await versionProbe.GetInstalledVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private void TryDeleteBackup(UpdaterArguments args)
    {
        try
        {
            directoryBackup.DeleteBackup(args.RollbackDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not delete rollback backup '{args.RollbackDirectory}': {ex.Message}");
        }
    }

    private UpdateResult Result(UpdaterArguments args, DateTimeOffset startedAt, string outcome, string? reason, string? fromVersion, string? installed, int? exitCode) =>
        new()
        {
            UpdateId = args.UpdateId,
            FromVersion = fromVersion,
            ToVersion = args.ExpectedVersion,
            InstalledVersion = installed,
            Outcome = outcome,
            Reason = reason,
            InstallerExitCode = exitCode,
            StartedAt = startedAt,
            CompletedAt = timeProvider.GetUtcNow()
        };

    private sealed record HealthOutcome(bool Healthy, string? Version, string Detail);
}
