using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// Line-delimited JSON protocol between Agent Core and the isolated backup worker process
/// (§3 "Identity/heartbeat/updates isolated from backup engine", §13 "keep Agent Core
/// responsive if backup worker crashes").
///
/// Transport is the worker's own stdin/stdout pipes: nothing secret ever touches the command
/// line (visible to every local admin via process listings) or disk. One JSON object per line.
/// </summary>
public static class WorkerMessageTypes
{
    // Core -> Worker
    public const string Start = "start";
    public const string GrantRenewed = "grant.renewed";
    public const string GrantRenewFailed = "grant.renew.failed";
    public const string Cancel = "cancel";

    // Worker -> Core
    public const string Stage = "stage";
    public const string Progress = "progress";
    public const string GrantRenewRequest = "grant.renew";
    public const string Log = "log";
    public const string Result = "result";

    /// <summary>One source's snapshot is complete (durable per-source checkpoint, §8).</summary>
    public const string Checkpoint = "checkpoint";
}

public sealed class WorkerEnvelope
{
    [JsonPropertyName("type")] public required string Type { get; init; }

    // Core -> Worker
    [JsonPropertyName("spec")] public WorkerJobSpec? Spec { get; init; }
    [JsonPropertyName("grant")] public StorageGrant? Grant { get; init; }

    // Worker -> Core
    [JsonPropertyName("stage")] public string? Stage { get; init; }
    [JsonPropertyName("progress")] public JobProgress? Progress { get; init; }
    [JsonPropertyName("uploadBytesPerSecond")] public double? UploadBytesPerSecond { get; init; }
    [JsonPropertyName("scannerDurationMs")] public long? ScannerDurationMs { get; init; }
    [JsonPropertyName("level")] public string? Level { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("result")] public WorkerResult? Result { get; init; }
    [JsonPropertyName("snapshot")] public SnapshotRef? Snapshot { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Everything the worker needs for one job. Carries the grant (SECRET) - pipe only.</summary>
public sealed class WorkerJobSpec
{
    [JsonPropertyName("jobId")] public required string JobId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("endpointId")] public required string EndpointId { get; init; }
    [JsonPropertyName("engine")] public required string Engine { get; init; }
    [JsonPropertyName("grant")] public required StorageGrant Grant { get; init; }
    [JsonPropertyName("paths")] public required WorkerPaths Paths { get; init; }
    [JsonPropertyName("budget")] public required WorkerBudget Budget { get; init; }

    // backup
    [JsonPropertyName("sources")] public IReadOnlyList<BackupSource> Sources { get; init; } = [];
    [JsonPropertyName("retention")] public BackupRetention? Retention { get; init; }
    [JsonPropertyName("consistency")] public BackupConsistencyPolicy? Consistency { get; init; }
    [JsonPropertyName("verification")] public BackupVerificationPolicy? Verification { get; init; }
    [JsonPropertyName("policyVersionId")] public string? PolicyVersionId { get; init; }

    // verify / restore
    [JsonPropertyName("engineSnapshotId")] public string? EngineSnapshotId { get; init; }
    [JsonPropertyName("verifyFilesPercent")] public double? VerifyFilesPercent { get; init; }
    [JsonPropertyName("restore")] public RestoreExecuteParameters? Restore { get; init; }

    /// <summary>Sources already snapshotted by an earlier attempt of this job - not re-run.</summary>
    [JsonPropertyName("completedSnapshots")] public IReadOnlyList<SnapshotRef> CompletedSnapshots { get; init; } = [];

    // maintenance
    [JsonPropertyName("maintenanceFull")] public bool MaintenanceFull { get; init; } = true;

    public override string ToString() => $"WorkerJobSpec(job={JobId}, kind={Kind}, engine={Engine}, grant=REDACTED)";
}

public sealed class WorkerPaths
{
    /// <summary>Folder holding kopia.exe / restic.exe / rclone.exe and engines.manifest.json.</summary>
    [JsonPropertyName("engineDirectory")] public required string EngineDirectory { get; init; }

    /// <summary>Persistent, bounded engine cache (speeds up incrementals; size-capped).</summary>
    [JsonPropertyName("cacheDirectory")] public required string CacheDirectory { get; init; }

    /// <summary>Per-job scratch directory (engine config etc.) - deleted when the job ends.</summary>
    [JsonPropertyName("workDirectory")] public required string WorkDirectory { get; init; }

    /// <summary>Where the gzip NDJSON manifest is written for Core to upload.</summary>
    [JsonPropertyName("manifestPath")] public string? ManifestPath { get; init; }
}

/// <summary>Engine-level knobs derived by the Resource Governor (§7). OS-level caps are applied by Core via a Job Object.</summary>
public sealed class WorkerBudget
{
    [JsonPropertyName("profile")] public required string Profile { get; init; }
    [JsonPropertyName("parallelism")] public int Parallelism { get; init; } = 1;
    [JsonPropertyName("maxUploadBytesPerSecond")] public long? MaxUploadBytesPerSecond { get; init; }

    /// <summary>GOMAXPROCS for the Go engines (Kopia/Restic/rclone are all Go programs).</summary>
    [JsonPropertyName("goMaxProcs")] public int GoMaxProcs { get; init; } = 1;

    /// <summary>GOMEMLIMIT (MiB) - Go runtime soft memory limit inside the hard Job Object cap.</summary>
    [JsonPropertyName("goMemoryLimitMb")] public int GoMemoryLimitMb { get; init; } = 256;
    [JsonPropertyName("contentCacheMb")] public int ContentCacheMb { get; init; } = 256;
    [JsonPropertyName("metadataCacheMb")] public int MetadataCacheMb { get; init; } = 256;

    /// <summary>Hard cap on manifest entries kept for the portal file explorer.</summary>
    [JsonPropertyName("maxManifestEntries")] public int MaxManifestEntries { get; init; } = 2_000_000;
}

public sealed class WorkerResult
{
    /// <summary>Terminal state: SUCCEEDED / PARTIAL / FAILED / CANCELLED.</summary>
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("summary")] public JobResultSummary? Summary { get; init; }
}

/// <summary>Local-only IPC between the optional tray app and Agent Core (§4 local UI contract).</summary>
public static class LocalStatusProtocol
{
    public const string PipeName = "CloudOrc.Agent.Status";

    public const string ActionStatus = "status";
    public const string ActionActivate = "activate";
    public const string ActionCancelActivation = "cancel-activation";
    public const string ActionRetry = "retry";
}

/// <summary>The four local screens of §4: Not activated / Connecting / Connected / Problem.</summary>
public static class LocalAgentStates
{
    public const string NotActivated = "NOT_ACTIVATED";
    public const string Connecting = "CONNECTING";
    public const string Connected = "CONNECTED";
    public const string Offline = "OFFLINE";
    public const string Problem = "PROBLEM";
}

public sealed class LocalStatusRequest
{
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
}

/// <summary>
/// What the tray may show. By design (§4) it contains NO schedule, retention, source paths,
/// keys, destinations, backup/restore controls or file browsing - those live in the portal.
/// </summary>
public sealed class LocalStatusResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; } = true;
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("deviceName")] public required string DeviceName { get; init; }
    [JsonPropertyName("agentVersion")] public required string AgentVersion { get; init; }
    [JsonPropertyName("lastContactAt")] public DateTimeOffset? LastContactAt { get; init; }
    [JsonPropertyName("retryStatus")] public string? RetryStatus { get; init; }

    /// <summary>Readable reason for the Problem screen.</summary>
    [JsonPropertyName("problem")] public string? Problem { get; init; }

    /// <summary>"Reconnect only if authorization expired" (§4).</summary>
    [JsonPropertyName("canReconnect")] public bool CanReconnect { get; init; }

    /// <summary>Opaque id support can look up - never contains secrets.</summary>
    [JsonPropertyName("diagnosticsId")] public required string DiagnosticsId { get; init; }

    /// <summary>"Protected status summary": e.g. "Protected - last backup 02:14, verified".</summary>
    [JsonPropertyName("protectionSummary")] public string? ProtectionSummary { get; init; }
    [JsonPropertyName("portalUrl")] public string? PortalUrl { get; init; }
}
