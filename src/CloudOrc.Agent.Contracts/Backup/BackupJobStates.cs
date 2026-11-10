namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// The backup lifecycle from the active Infrastructure spec, as restated in CO-DEV-BKP-001 §1:
/// QUEUED → SNAPSHOTTING → SCANNING → TRANSFERRING → VERIFYING → SUCCEEDED / RETRYING / PARTIAL / FAILED.
/// CANCELLED is added for an operator-initiated stop. Values are the exact wire strings.
/// </summary>
public static class BackupJobStates
{
    public const string Queued = "QUEUED";
    public const string Snapshotting = "SNAPSHOTTING";
    public const string Scanning = "SCANNING";
    public const string Transferring = "TRANSFERRING";
    public const string Verifying = "VERIFYING";
    public const string Succeeded = "SUCCEEDED";
    public const string Retrying = "RETRYING";
    public const string Partial = "PARTIAL";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";

    /// <summary>Restore-only stage: engine is writing files to the target.</summary>
    public const string Restoring = "RESTORING";

    /// <summary>Maintenance-only stage: retention/prune/compaction running.</summary>
    public const string Maintaining = "MAINTAINING";

    public static bool IsTerminal(string state) =>
        state is Succeeded or Partial or Failed or Cancelled;

    /// <summary>States in which the worker process is (or was, before a crash) actively running.</summary>
    public static bool IsActive(string state) =>
        state is Snapshotting or Scanning or Transferring or Verifying or Restoring or Maintaining;

    public static bool IsRunnable(string state) => state is Queued or Retrying;
}

/// <summary>Kinds of durable jobs the agent tracks locally.</summary>
public static class BackupJobKinds
{
    public const string Backup = "backup";
    public const string Verify = "verify";
    public const string Maintenance = "maintenance";
    public const string Restore = "restore";
}

/// <summary>
/// Recovery-point consistency level. CO-DEV-BKP-001 §6: "If a source cannot be captured
/// consistently, mark degraded/failed according to policy; do not silently claim verified success."
/// </summary>
public static class ConsistencyLevels
{
    /// <summary>VSS snapshot taken and every application-consistency precondition held.</summary>
    public const string ApplicationConsistent = "APPLICATION_CONSISTENT";

    /// <summary>VSS snapshot taken (point-in-time) but an application (e.g. Tally) was live.</summary>
    public const string CrashConsistent = "CRASH_CONSISTENT";

    /// <summary>No point-in-time snapshot - files read live and may be torn.</summary>
    public const string Inconsistent = "INCONSISTENT";
}

/// <summary>Verification state of a recovery point.</summary>
public static class VerificationStates
{
    public const string NotVerified = "NOT_VERIFIED";
    public const string Verified = "VERIFIED";
    public const string VerificationFailed = "VERIFICATION_FAILED";
}

/// <summary>Effective resource profile names (CO-DEV-BKP-001 §7).</summary>
public static class ResourceProfileNames
{
    public const string Eco = "eco";
    public const string Balanced = "balanced";
    public const string Throughput = "throughput";
}

/// <summary>Supported backup engines (ADR-01: Kopia preferred, Restic mandatory challenger).</summary>
public static class BackupEngineNames
{
    public const string Kopia = "kopia";
    public const string Restic = "restic";
}

/// <summary>Storage provider types the agent can configure an engine for (§9).</summary>
public static class StorageProviderTypes
{
    /// <summary>Customer-controlled Google Drive (separate from managed DR, §9).</summary>
    public const string GoogleDrive = "gdrive";

    /// <summary>Any S3-compatible endpoint: Wasabi, Backblaze B2, Amazon S3, CloudOrc own DC.</summary>
    public const string S3 = "s3";
}
