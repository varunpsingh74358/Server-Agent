using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// The EFFECTIVE backup policy for this endpoint, as already resolved by the backend Policy
/// Resolver (Management → Partner → Plan/SKU → Customer → Endpoint, §1/§3). The agent never
/// performs inheritance itself - it only executes the resolved version it was given, and
/// reports the <see cref="PolicyVersionId"/>/<see cref="EffectiveHash"/> it ran with on every
/// job event (data model §11: backup_policy_version / backup_assignment).
///
/// Contains no secrets: the repository password and storage credentials arrive only in a
/// short-lived storage grant at job time (§10), never in the policy.
/// </summary>
public sealed class BackupPolicyDocument
{
    [JsonPropertyName("policyId")]
    public required string PolicyId { get; init; }

    [JsonPropertyName("policyVersionId")]
    public required string PolicyVersionId { get; init; }

    [JsonPropertyName("effectiveHash")]
    public required string EffectiveHash { get; init; }

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = BackupSchema.Version;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("sources")]
    public IReadOnlyList<BackupSource> Sources { get; init; } = [];

    [JsonPropertyName("schedule")]
    public required BackupSchedule Schedule { get; init; }

    [JsonPropertyName("retention")]
    public BackupRetention Retention { get; init; } = new();

    [JsonPropertyName("resourceProfile")]
    public string ResourceProfile { get; init; } = ResourceProfileNames.Eco;

    [JsonPropertyName("repository")]
    public required BackupRepositoryRef Repository { get; init; }

    [JsonPropertyName("consistency")]
    public BackupConsistencyPolicy Consistency { get; init; } = new();

    [JsonPropertyName("verification")]
    public BackupVerificationPolicy Verification { get; init; } = new();

    /// <summary>
    /// ADR-04 (open): how long previously-authorized work may proceed while the control plane
    /// is unreachable. Default 0 = never start a job without a fresh admission + grant. Even
    /// when &gt; 0 the agent still needs a valid, unexpired storage grant, so this can only ever
    /// let an already-granted job finish, never start a new one offline.
    /// </summary>
    [JsonPropertyName("offlineGraceMinutes")]
    public int OfflineGraceMinutes { get; init; }

    /// <summary>Maximum automatic attempts for one scheduled run before FAILED/RETRIES_EXHAUSTED.</summary>
    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; init; } = 3;
}

public sealed class BackupSource
{
    /// <summary>Absolute local path (folder or file). Tally data folders use kind "tally".</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>"folder", "file" or "tally".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = BackupSourceKinds.Folder;

    /// <summary>Glob-style exclusions relative to <see cref="Path"/> (engine-native ignore rules).</summary>
    [JsonPropertyName("excludes")]
    public IReadOnlyList<string> Excludes { get; init; } = [];
}

public static class BackupSourceKinds
{
    public const string Folder = "folder";
    public const string File = "file";
    public const string Tally = "tally";
}

public sealed class BackupSchedule
{
    /// <summary>"daily", "interval" or "manual" (manual = on-demand backup.run only).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = BackupScheduleKinds.Daily;

    /// <summary>For "daily": local wall-clock start times, "HH:mm".</summary>
    [JsonPropertyName("timesOfDay")]
    public IReadOnlyList<string> TimesOfDay { get; init; } = ["01:00"];

    /// <summary>For "daily": ISO day names allowed ("Monday".."Sunday"); empty = every day.</summary>
    [JsonPropertyName("daysOfWeek")]
    public IReadOnlyList<string> DaysOfWeek { get; init; } = [];

    /// <summary>For "interval": hours between runs.</summary>
    [JsonPropertyName("intervalHours")]
    public double? IntervalHours { get; init; }

    /// <summary>
    /// Spread window (§8: "Stable per-agent schedule offset prevents midnight synchronization").
    /// Each agent gets a deterministic offset in [0, windowMinutes) derived from its own id,
    /// so the same agent always starts at the same time but the fleet is spread out.
    /// </summary>
    [JsonPropertyName("windowMinutes")]
    public int WindowMinutes { get; init; } = 120;

    /// <summary>Windows time zone id for timesOfDay; null = machine local time.</summary>
    [JsonPropertyName("timeZoneId")]
    public string? TimeZoneId { get; init; }

    /// <summary>Whether a slot missed while the agent was off/offline is caught up (once).</summary>
    [JsonPropertyName("runMissed")]
    public bool RunMissed { get; init; } = true;

    /// <summary>
    /// Post-outage catch-up spread (§8: "post-outage recovery uses independent randomized
    /// spread"). A missed slot starts at a fresh random point within this many minutes.
    /// </summary>
    [JsonPropertyName("catchUpSpreadMinutes")]
    public int CatchUpSpreadMinutes { get; init; } = 60;
}

public static class BackupScheduleKinds
{
    public const string Daily = "daily";
    public const string Interval = "interval";
    public const string Manual = "manual";
}

/// <summary>Retention counts, engine-native (Kopia policy keep-* / Restic forget --keep-*).</summary>
public sealed class BackupRetention
{
    [JsonPropertyName("keepLatest")] public int? KeepLatest { get; init; } = 7;
    [JsonPropertyName("keepHourly")] public int? KeepHourly { get; init; }
    [JsonPropertyName("keepDaily")] public int? KeepDaily { get; init; } = 14;
    [JsonPropertyName("keepWeekly")] public int? KeepWeekly { get; init; } = 8;
    [JsonPropertyName("keepMonthly")] public int? KeepMonthly { get; init; } = 12;
    [JsonPropertyName("keepAnnual")] public int? KeepAnnual { get; init; }
}

/// <summary>Points at the backend-owned repository (§11 backup_repository) - never credentials.</summary>
public sealed class BackupRepositoryRef
{
    [JsonPropertyName("repositoryId")]
    public required string RepositoryId { get; init; }

    [JsonPropertyName("storagePoolId")]
    public required string StoragePoolId { get; init; }

    /// <summary>"kopia" or "restic" - ADR-01: never hardcoded into the control-plane schema.</summary>
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = BackupEngineNames.Kopia;

    [JsonPropertyName("formatVersion")]
    public string? FormatVersion { get; init; }
}

/// <summary>
/// §6 consistency rules. ADR-05 (Tally procedure) is still open, so the agent exposes the
/// decision as policy rather than hardcoding one behavior.
/// </summary>
public sealed class BackupConsistencyPolicy
{
    /// <summary>
    /// "always": a VSS snapshot is required, fail otherwise. "when-available": fall back to
    /// live reads (recovery point marked INCONSISTENT). "never": live reads only.
    /// </summary>
    [JsonPropertyName("vssMode")]
    public string VssMode { get; init; } = VssModes.Always;

    /// <summary>What to do when consistency cannot be achieved: "fail" or "degrade" (PARTIAL).</summary>
    [JsonPropertyName("onInconsistent")]
    public string OnInconsistent { get; init; } = InconsistencyActions.Degrade;

    /// <summary>
    /// Max time a VSS snapshot may live (§6 "Enforce snapshot lifetime"). The engine run is
    /// stopped and the shadow copy released if this is exceeded.
    /// </summary>
    [JsonPropertyName("maxSnapshotLifetimeMinutes")]
    public int MaxSnapshotLifetimeMinutes { get; init; } = 720;

    /// <summary>
    /// Minimum free space required on each source volume for copy-on-write before a
    /// snapshot is attempted (§6 "copy-on-write space limits").
    /// </summary>
    [JsonPropertyName("minCopyOnWriteFreePercent")]
    public double MinCopyOnWriteFreePercent { get; init; } = 10;

    [JsonPropertyName("tally")]
    public TallyConsistencyPolicy Tally { get; init; } = new();
}

public static class VssModes
{
    public const string Always = "always";
    public const string WhenAvailable = "when-available";
    public const string Never = "never";
}

public static class InconsistencyActions
{
    public const string Fail = "fail";
    public const string Degrade = "degrade";
}

/// <summary>
/// "filesystem snapshots alone do not guarantee Tally recoverability" (§6). Until ADR-05
/// fixes the procedure, the agent detects a running Tally and classifies the result.
/// </summary>
public sealed class TallyConsistencyPolicy
{
    /// <summary>
    /// "require-closed": Tally must not be running for an APPLICATION_CONSISTENT point.
    /// "crash-consistent-ok": a VSS point taken while Tally runs is acceptable.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = TallyModes.RequireClosed;

    /// <summary>Process names (without .exe) that indicate Tally is live.</summary>
    [JsonPropertyName("processNames")]
    public IReadOnlyList<string> ProcessNames { get; init; } = ["tally"];

    /// <summary>"wait-for-close" only: how long to wait for Tally to close before backing up anyway.</summary>
    [JsonPropertyName("maxWaitMinutes")]
    public int MaxWaitMinutes { get; init; } = 30;
}

public static class TallyModes
{
    public const string RequireClosed = "require-closed";
    public const string CrashConsistentOk = "crash-consistent-ok";

    /// <summary>
    /// Wait (bounded by maxWaitMinutes) for Tally to be closed before snapshotting, so an overnight
    /// run gets an application-consistent point once users leave; still running after the wait →
    /// crash-consistent (degraded) exactly like "require-closed".
    /// </summary>
    public const string WaitForClose = "wait-for-close";
}

public sealed class BackupVerificationPolicy
{
    /// <summary>Percent of files to read back after each backup (0 disables content read-back).</summary>
    [JsonPropertyName("verifyFilesPercent")]
    public double VerifyFilesPercent { get; init; } = 1;
}
