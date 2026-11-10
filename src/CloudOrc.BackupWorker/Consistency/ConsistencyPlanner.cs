using System.Diagnostics;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker.Consistency;

/// <summary>Result of the pre-backup consistency check for one job.</summary>
public sealed record ConsistencyPlan(
    bool UseVss,
    string ExpectedLevel,
    string? FailReasonCode,
    string? FailMessage,
    IReadOnlyList<string> Notes,
    TimeSpan SnapshotLifetime);

/// <summary>
/// §6 "Snapshot source: use supported VSS workflow for Windows volumes and application-specific
/// consistency for Tally; filesystem snapshots alone do not guarantee Tally recoverability.
/// Enforce snapshot lifetime/copy-on-write space limits. If a source cannot be captured
/// consistently, mark degraded/failed according to policy; do not silently claim verified success."
///
/// The VSS snapshot itself is taken by the engine's native Windows support (Kopia
/// enable-volume-shadow-copy / Restic --use-fs-snapshot): that keeps the original source paths in
/// the repository so incrementals and restores line up. This planner decides whether VSS is
/// required and feasible (filesystem, copy-on-write headroom), what consistency level the result
/// can honestly claim (Tally live → CRASH_CONSISTENT at best), and the snapshot lifetime cap,
/// which the worker enforces by stopping the engine (auto-release shadow copies go with it).
/// </summary>
public static class ConsistencyPlanner
{
    public static ConsistencyPlan Plan(
        IReadOnlyList<BackupSource> sources,
        BackupConsistencyPolicy policy,
        Func<string, VolumeFacts?> volumeFacts,
        Func<IReadOnlyList<string>, bool> isAnyProcessRunning)
    {
        var notes = new List<string>();
        var lifetime = TimeSpan.FromMinutes(Math.Clamp(policy.MaxSnapshotLifetimeMinutes, 10, 24 * 60));

        // ---------------------------------------------------------------- VSS feasibility
        var vssWanted = policy.VssMode != VssModes.Never;
        var vssFeasible = true;
        foreach (var root in sources.Select(s => Path.GetPathRoot(s.Path)).Where(r => r is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var facts = volumeFacts(root!);
            if (facts is null)
            {
                vssFeasible = false;
                notes.Add($"Volume {root} is not available.");
                continue;
            }

            if (!facts.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !facts.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            {
                vssFeasible = false;
                notes.Add($"Volume {root} ({facts.FileSystem}) does not support shadow copies.");
            }

            var freePercent = facts.TotalBytes > 0 ? facts.FreeBytes * 100.0 / facts.TotalBytes : 0;
            if (freePercent < policy.MinCopyOnWriteFreePercent)
            {
                vssFeasible = false;
                notes.Add($"Volume {root} has {freePercent:F1}% free; at least {policy.MinCopyOnWriteFreePercent:F0}% is required for snapshot copy-on-write.");
            }
        }

        if (vssWanted && !vssFeasible && policy.VssMode == VssModes.Always)
        {
            var reason = notes.Any(n => n.Contains("copy-on-write", StringComparison.Ordinal))
                ? BackupReasonCodes.SnapshotStorageInsufficient
                : BackupReasonCodes.SnapshotFailed;
            return new ConsistencyPlan(false, ConsistencyLevels.Inconsistent, reason,
                "A consistent snapshot is required by policy but cannot be taken: " + string.Join(" ", notes), notes, lifetime);
        }

        var useVss = vssWanted && vssFeasible;
        var level = useVss ? ConsistencyLevels.ApplicationConsistent : ConsistencyLevels.Inconsistent;
        if (!useVss)
        {
            notes.Add("Files are read without a snapshot; open files may be captured inconsistently.");
        }

        // ---------------------------------------------------------------- Tally (ADR-05 pending)
        if (sources.Any(s => s.Kind == BackupSourceKinds.Tally) && isAnyProcessRunning(policy.Tally.ProcessNames))
        {
            if (policy.Tally.Mode is TallyModes.RequireClosed or TallyModes.WaitForClose)
            {
                notes.Add("Tally is running; the snapshot is crash-consistent, not application-consistent.");
            }

            if (level == ConsistencyLevels.ApplicationConsistent)
            {
                level = ConsistencyLevels.CrashConsistent;
            }
        }

        if (level != ConsistencyLevels.ApplicationConsistent && policy.OnInconsistent == InconsistencyActions.Fail
            && !(level == ConsistencyLevels.CrashConsistent && policy.Tally.Mode == TallyModes.CrashConsistentOk))
        {
            return new ConsistencyPlan(useVss, level,
                level == ConsistencyLevels.CrashConsistent ? BackupReasonCodes.ApplicationNotQuiesced : BackupReasonCodes.SnapshotFailed,
                "Policy requires an application-consistent backup: " + string.Join(" ", notes), notes, lifetime);
        }

        return new ConsistencyPlan(useVss, level, null, null, notes, lifetime);
    }

    /// <summary>Whether a result at <paramref name="level"/> must be reported as PARTIAL (degraded) rather than SUCCEEDED.</summary>
    public static bool IsDegraded(string level, BackupConsistencyPolicy policy) =>
        level switch
        {
            ConsistencyLevels.ApplicationConsistent => false,
            ConsistencyLevels.CrashConsistent => policy.Tally.Mode != TallyModes.CrashConsistentOk,
            _ => policy.VssMode != VssModes.Never
        };

    /// <summary>
    /// "wait-for-close": polls until no protected process runs or the wait expires. Returns true
    /// when the application was (or became) closed. Cancellable; never waits for other modes.
    /// </summary>
    public static async Task<bool> WaitForApplicationCloseAsync(
        IReadOnlyList<BackupSource> sources,
        BackupConsistencyPolicy policy,
        Func<IReadOnlyList<string>, bool> isAnyProcessRunning,
        TimeSpan pollInterval,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (policy.Tally.Mode != TallyModes.WaitForClose || !sources.Any(s => s.Kind == BackupSourceKinds.Tally))
        {
            return !isAnyProcessRunning(policy.Tally.ProcessNames);
        }

        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Max(0, policy.Tally.MaxWaitMinutes));
        var announced = false;
        while (isAnyProcessRunning(policy.Tally.ProcessNames))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                log?.Invoke($"Tally is still running after waiting {policy.Tally.MaxWaitMinutes} min; continuing with a crash-consistent snapshot.");
                return false;
            }

            if (!announced)
            {
                announced = true;
                log?.Invoke($"Tally is running; waiting up to {policy.Tally.MaxWaitMinutes} min for it to close for an application-consistent backup.");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public static VolumeFacts? ReadVolume(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? new VolumeFacts(drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool AnyProcessRunning(IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var processes = Process.GetProcessesByName(name);
            var any = processes.Length > 0;
            foreach (var p in processes)
            {
                p.Dispose();
            }

            if (any)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record VolumeFacts(string FileSystem, long TotalBytes, long FreeBytes);
