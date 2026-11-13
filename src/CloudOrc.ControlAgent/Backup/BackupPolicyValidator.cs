using System.Globalization;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// Local re-validation of an effective policy before it is stored. The backend already
/// validated it, but the agent is the last line of defence (§10 "allow-listed ... checked at
/// every command") and must refuse anything it cannot execute safely.
/// </summary>
public static class BackupPolicyValidator
{
    public static IReadOnlyList<string> Validate(BackupPolicyDocument? policy)
    {
        var errors = new List<string>();
        if (policy is null)
        {
            return ["policy is required."];
        }

        Require(errors, IsSafeId(policy.PolicyId), "policyId must be 1-128 safe characters.");
        Require(errors, IsSafeId(policy.PolicyVersionId), "policyVersionId must be 1-128 safe characters.");
        Require(errors, !string.IsNullOrWhiteSpace(policy.EffectiveHash) && policy.EffectiveHash.Length <= 256, "effectiveHash is required.");
        Require(errors, policy.SchemaVersion.StartsWith("1.", StringComparison.Ordinal) || policy.SchemaVersion == "1",
            $"Unsupported policy schemaVersion '{policy.SchemaVersion}'.");
        Require(errors, policy.MaxAttempts is >= 1 and <= 10, "maxAttempts must be 1-10.");
        Require(errors, policy.OfflineGraceMinutes is >= 0 and <= 7 * 24 * 60, "offlineGraceMinutes must be 0-10080.");

        if (policy.Sources.Count == 0)
        {
            errors.Add("At least one source is required.");
        }

        Require(errors, policy.Sources.Count <= 64, "At most 64 sources are supported.");
        foreach (var source in policy.Sources)
        {
            ValidateSource(source, errors);
        }

        ValidateSchedule(policy.Schedule, errors);
        ValidateRetention(policy.Retention, errors);

        Require(errors, policy.ResourceProfile is ResourceProfileNames.Eco or ResourceProfileNames.Balanced or ResourceProfileNames.Throughput,
            $"Unknown resourceProfile '{policy.ResourceProfile}'.");
        Require(errors, BackupModes.IsKnown(policy.BackupMode), $"Unknown backupMode '{policy.BackupMode}'.");
        Require(errors, Enum.TryParse<DayOfWeek>(policy.FullBackupDay, true, out _), $"Unknown fullBackupDay '{policy.FullBackupDay}'.");
        Require(errors, IsSafeId(policy.Repository.RepositoryId), "repository.repositoryId is required.");
        Require(errors, IsSafeId(policy.Repository.StoragePoolId), "repository.storagePoolId is required.");
        Require(errors, policy.Repository.Engine is BackupEngineNames.Kopia or BackupEngineNames.Restic,
            $"Unknown repository.engine '{policy.Repository.Engine}'.");

        var c = policy.Consistency;
        Require(errors, c.VssMode is VssModes.Always or VssModes.WhenAvailable or VssModes.Never, $"Unknown consistency.vssMode '{c.VssMode}'.");
        Require(errors, c.OnInconsistent is InconsistencyActions.Fail or InconsistencyActions.Degrade, $"Unknown consistency.onInconsistent '{c.OnInconsistent}'.");
        Require(errors, c.MaxSnapshotLifetimeMinutes is >= 10 and <= 24 * 60, "consistency.maxSnapshotLifetimeMinutes must be 10-1440.");
        Require(errors, c.MinCopyOnWriteFreePercent is >= 0 and <= 50, "consistency.minCopyOnWriteFreePercent must be 0-50.");
        Require(errors, c.Tally.Mode is TallyModes.RequireClosed or TallyModes.CrashConsistentOk or TallyModes.WaitForClose, $"Unknown consistency.tally.mode '{c.Tally.Mode}'.");
        Require(errors, c.Tally.MaxWaitMinutes is >= 0 and <= 720, "consistency.tally.maxWaitMinutes must be 0-720.");
        Require(errors, c.Tally.ProcessNames.All(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 64 && n.IndexOfAny(['\\', '/', ':']) < 0),
            "consistency.tally.processNames must be plain process names.");

        Require(errors, policy.Verification.VerifyFilesPercent is >= 0 and <= 100, "verification.verifyFilesPercent must be 0-100.");
        return errors;
    }

    /// <summary>
    /// Phase 1 is local file/folder/Tally backup (§2). UNC/network paths, device paths and
    /// relative paths are refused: VSS cannot snapshot them and they widen the attack surface.
    /// </summary>
    public static bool IsAllowedLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024)
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false; // UNC, \\?\ and \\.\ device paths
        }

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
        {
            return false;
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf(':', 2) >= 0)
        {
            return false; // also rejects NTFS alternate data streams
        }

        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.All(s => s != ".." && s != ".");
    }

    private static void ValidateSource(BackupSource source, List<string> errors)
    {
        if (!IsAllowedLocalPath(source.Path))
        {
            errors.Add($"Source path '{Trim(source.Path)}' must be an absolute local drive path (no UNC, device, relative or '..' paths).");
        }

        if (source.Kind is not (BackupSourceKinds.Folder or BackupSourceKinds.File or BackupSourceKinds.Tally))
        {
            errors.Add($"Unknown source kind '{source.Kind}'.");
        }

        if (source.Excludes.Count > 256 || source.Excludes.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 260 || e.Contains("..", StringComparison.Ordinal)))
        {
            errors.Add($"Excludes for '{Trim(source.Path)}' are invalid (max 256 patterns, no '..').");
        }
    }

    private static void ValidateSchedule(BackupSchedule schedule, List<string> errors)
    {
        switch (schedule.Kind)
        {
            case BackupScheduleKinds.Daily:
                if (schedule.TimesOfDay.Count is 0 or > 24 || schedule.TimesOfDay.Any(t => !TryParseTimeOfDay(t, out _)))
                {
                    errors.Add("schedule.timesOfDay must contain 1-24 'HH:mm' values.");
                }

                if (schedule.DaysOfWeek.Any(d => !Enum.TryParse<DayOfWeek>(d, ignoreCase: true, out _)))
                {
                    errors.Add("schedule.daysOfWeek contains an unknown day.");
                }

                break;
            case BackupScheduleKinds.Interval:
                if (schedule.IntervalHours is null or < 0.25 or > 24 * 31)
                {
                    errors.Add("schedule.intervalHours must be between 0.25 and 744.");
                }

                break;
            case BackupScheduleKinds.Manual:
                break;
            default:
                errors.Add($"Unknown schedule.kind '{schedule.Kind}'.");
                break;
        }

        Require(errors, schedule.WindowMinutes is >= 0 and <= 24 * 60, "schedule.windowMinutes must be 0-1440.");
        Require(errors, schedule.CatchUpSpreadMinutes is >= 0 and <= 24 * 60, "schedule.catchUpSpreadMinutes must be 0-1440.");
        if (schedule.TimeZoneId is not null && !TryFindTimeZone(schedule.TimeZoneId, out _))
        {
            errors.Add($"schedule.timeZoneId '{schedule.TimeZoneId}' is not known on this machine.");
        }
    }

    private static void ValidateRetention(BackupRetention r, List<string> errors)
    {
        int?[] all = [r.KeepLatest, r.KeepHourly, r.KeepDaily, r.KeepWeekly, r.KeepMonthly, r.KeepAnnual];
        Require(errors, all.All(v => v is null or (>= 0 and <= 10_000)), "retention values must be 0-10000.");
        Require(errors, all.Any(v => v > 0), "retention must keep at least one recovery point.");
    }

    public static bool TryParseTimeOfDay(string? value, out TimeSpan time) =>
        TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);

    public static bool TryFindTimeZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Local;
            return false;
        }
    }

    public static bool IsSafeId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');

    private static void Require(List<string> errors, bool condition, string message)
    {
        if (!condition)
        {
            errors.Add(message);
        }
    }

    private static string Trim(string? value) => value is null ? "" : value.Length > 80 ? value[..80] + "..." : value;
}
