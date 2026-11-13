using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// HTTPS control-plane routes the agent calls (§12). The first four are from the guide's own
/// API list; the ones marked "agent addition" are not in §12 and must be added to the backend
/// contract (and to the next Infrastructure revision) before go-live.
/// </summary>
public static class ControlPlaneRoutes
{
    public const string AdmissionRequest = "api/v1/backup/admission/request";
    public const string StorageGrants = "api/v1/storage/grants";
    public const string AgentEnroll = "api/v1/agent/enroll";
    public const string SessionRenew = "api/v1/agent/session/renew";

    /// <summary>Agent addition: mTLS client certificate rotation (§4 "credential issuance and rotation").</summary>
    public const string CertificateRenew = "api/v1/agent/certificate/renew";

    public static string JobEvents(string jobId) => $"api/v1/backup/jobs/{Uri.EscapeDataString(jobId)}/events";

    /// <summary>Agent addition: lease renewal (§8 "Lease has TTL, renewal").</summary>
    public static string LeaseRenew(string leaseId) => $"api/v1/backup/admission/{Uri.EscapeDataString(leaseId)}/renew";

    /// <summary>Agent addition: early lease release when a job ends.</summary>
    public static string LeaseRelease(string leaseId) => $"api/v1/backup/admission/{Uri.EscapeDataString(leaseId)}/release";

    /// <summary>Agent addition: per-recovery-point file manifest for the portal File Explorer.</summary>
    public static string JobManifest(string jobId) => $"api/v1/backup/jobs/{Uri.EscapeDataString(jobId)}/manifest";

    /// <summary>Agent addition: restore job progress/results.</summary>
    public static string RestoreEvents(string restoreJobId) => $"api/v1/backup/restores/{Uri.EscapeDataString(restoreJobId)}/events";

    /// <summary>Agent addition: agent lifecycle events (update results, etc.).</summary>
    public const string AgentEvents = "api/v1/agent/events";
}

// ---------------------------------------------------------------- Admission (§8, §12)

public sealed class AdmissionRequest
{
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("endpoint_id")] public required string EndpointId { get; init; }
    [JsonPropertyName("policy_version")] public string? PolicyVersion { get; init; }
    [JsonPropertyName("estimated_bytes")] public long EstimatedBytes { get; init; }
    [JsonPropertyName("idempotency_key")] public required string IdempotencyKey { get; init; }

    /// <summary>backup / verify / maintenance / restore - lets the scheduler prioritize.</summary>
    [JsonPropertyName("operation")] public required string Operation { get; init; }
    [JsonPropertyName("repository_id")] public string? RepositoryId { get; init; }
    [JsonPropertyName("resource_profile")] public string? ResourceProfile { get; init; }
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = BackupSchema.Version;
}

public static class AdmissionDecisions
{
    public const string Granted = "GRANTED";
    public const string Deferred = "DEFERRED";
    public const string Denied = "DENIED";
}

public sealed class AdmissionResponse
{
    [JsonPropertyName("decision")] public string Decision { get; init; } = AdmissionDecisions.Deferred;
    [JsonPropertyName("lease")] public AdmissionLease? Lease { get; init; }

    /// <summary>DEFERRED: earliest time to ask again.</summary>
    [JsonPropertyName("not_before")] public DateTimeOffset? NotBefore { get; init; }

    /// <summary>DEFERRED: extra random spread the agent adds on top of not_before.</summary>
    [JsonPropertyName("jitter_window_seconds")] public int? JitterWindowSeconds { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed class AdmissionLease
{
    [JsonPropertyName("lease_id")] public required string LeaseId { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Monotonic token attached to every job event so the backend rejects a stale writer.</summary>
    [JsonPropertyName("fencing_token")] public long FencingToken { get; init; }
    [JsonPropertyName("max_parallel")] public int? MaxParallel { get; init; }
    [JsonPropertyName("max_upload_mbps")] public double? MaxUploadMbps { get; init; }
}

public sealed class LeaseRenewRequest
{
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("fencing_token")] public long FencingToken { get; init; }
}

// ---------------------------------------------------------------- Storage grants (§9, §10, §12)

public sealed class StorageGrantRequest
{
    [JsonPropertyName("repository_id")] public required string RepositoryId { get; init; }

    /// <summary>backup / restore / verify / maintenance - backend scopes the grant to it.</summary>
    [JsonPropertyName("operation")] public required string Operation { get; init; }
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("lease_id")] public string? LeaseId { get; init; }

    /// <summary>Set when renewing: the grant being replaced.</summary>
    [JsonPropertyName("previous_grant_id")] public string? PreviousGrantId { get; init; }
}

/// <summary>
/// A short-lived, repository- and operation-scoped storage grant (§10 "scoped storage access
/// with bounded exposure"). SECRET-BEARING: never log, never persist to disk, never put in an
/// event. It lives only in agent memory and is handed to the worker over its stdin pipe.
/// </summary>
public sealed class StorageGrant
{
    [JsonPropertyName("grant_id")] public required string GrantId { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("repository")] public required GrantRepository Repository { get; init; }
    [JsonPropertyName("gdrive")] public GoogleDriveGrant? GoogleDrive { get; init; }
    [JsonPropertyName("s3")] public S3Grant? S3 { get; init; }

    /// <summary>Provider capability flags (§9 "expose provider capability flags, not invented universal guarantees").</summary>
    [JsonPropertyName("capabilities")] public IReadOnlyDictionary<string, bool> Capabilities { get; init; } = new Dictionary<string, bool>();

    public override string ToString() => $"StorageGrant(id={GrantId}, provider={Provider}, expires={ExpiresAt:O}, secrets=REDACTED)";
}

public sealed class GrantRepository
{
    [JsonPropertyName("repository_id")] public required string RepositoryId { get; init; }
    [JsonPropertyName("engine")] public required string Engine { get; init; }

    /// <summary>Provider-relative repository location (Drive folder path / S3 key prefix).</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary>Engine repository password, escrowed server-side (§6 key protection). SECRET.</summary>
    [JsonPropertyName("password")] public required string Password { get; init; }

    /// <summary>True only for the first job of a brand-new repository.</summary>
    [JsonPropertyName("create_if_missing")] public bool CreateIfMissing { get; init; }

    /// <summary>Stable hostname/user the engine records snapshots under (keeps history stable across reinstalls).</summary>
    [JsonPropertyName("snapshot_host")] public string? SnapshotHost { get; init; }
    [JsonPropertyName("snapshot_user")] public string? SnapshotUser { get; init; }
}

public sealed class GoogleDriveGrant
{
    /// <summary>~1h OAuth access token minted from the server-held refresh token. SECRET.</summary>
    [JsonPropertyName("access_token")] public required string AccessToken { get; init; }
    [JsonPropertyName("token_expires_at")] public required DateTimeOffset TokenExpiresAt { get; init; }

    /// <summary>Folder id the repository lives under (rclone root_folder_id).</summary>
    [JsonPropertyName("root_folder_id")] public string? RootFolderId { get; init; }
    [JsonPropertyName("scope")] public string Scope { get; init; } = "drive.file";
}

public sealed class S3Grant
{
    [JsonPropertyName("endpoint")] public required string Endpoint { get; init; }
    [JsonPropertyName("region")] public string? Region { get; init; }
    [JsonPropertyName("bucket")] public required string Bucket { get; init; }
    [JsonPropertyName("prefix")] public string? Prefix { get; init; }
    [JsonPropertyName("access_key_id")] public required string AccessKeyId { get; init; }
    [JsonPropertyName("secret_access_key")] public required string SecretAccessKey { get; init; }
    [JsonPropertyName("session_token")] public string? SessionToken { get; init; }
}

// ---------------------------------------------------------------- Job events (§12)

public sealed class BackupJobEvent
{
    /// <summary>Unique per event - the backend inbox de-duplicates on it (§11 outbox/inbox).</summary>
    [JsonPropertyName("event_id")] public required string EventId { get; init; }
    [JsonPropertyName("event_type")] public required string EventType { get; init; }
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = BackupSchema.Version;
    [JsonPropertyName("correlation_id")] public required string CorrelationId { get; init; }
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("job_kind")] public required string JobKind { get; init; }
    [JsonPropertyName("endpoint_id")] public required string EndpointId { get; init; }

    /// <summary>Strictly increasing per job; backend applies events in sequence order.</summary>
    [JsonPropertyName("sequence")] public required long Sequence { get; init; }
    [JsonPropertyName("stage")] public required string Stage { get; init; }
    [JsonPropertyName("occurred_at")] public required DateTimeOffset OccurredAt { get; init; }
    [JsonPropertyName("policy_version")] public string? PolicyVersion { get; init; }
    [JsonPropertyName("policy_effective_hash")] public string? PolicyEffectiveHash { get; init; }
    [JsonPropertyName("repository_id")] public string? RepositoryId { get; init; }
    [JsonPropertyName("lease_id")] public string? LeaseId { get; init; }
    [JsonPropertyName("fencing_token")] public long? FencingToken { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; init; }

    /// <summary>Human-readable, already redacted. Never a secret, never a raw engine log.</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("progress")] public JobProgress? Progress { get; init; }
    [JsonPropertyName("resource")] public ResourceSample? Resource { get; init; }
    [JsonPropertyName("result")] public JobResultSummary? Result { get; init; }
}

public static class BackupEventTypes
{
    public const string Started = "backup.started";
    public const string Progress = "backup.progress";
    public const string StageChanged = "backup.stage";
    public const string Verified = "backup.verified";
    public const string Succeeded = "backup.succeeded";
    public const string Failed = "backup.failed";
    public const string Retrying = "backup.retrying";
    public const string Deferred = "backup.deferred";
    public const string Cancelled = "backup.cancelled";
    public const string RestoreStarted = "restore.started";
    public const string RestoreProgress = "restore.progress";
    public const string RestoreVerified = "restore.verified";
    public const string RestoreFailed = "restore.failed";
    public const string MaintenanceCompleted = "maintenance.completed";
    public const string VerifyCompleted = "verify.completed";
}

public sealed class JobProgress
{
    [JsonPropertyName("files_total")] public long? FilesTotal { get; init; }
    [JsonPropertyName("files_done")] public long? FilesDone { get; init; }
    [JsonPropertyName("bytes_total")] public long? BytesTotal { get; init; }
    [JsonPropertyName("bytes_read")] public long? BytesRead { get; init; }
    [JsonPropertyName("bytes_uploaded")] public long? BytesUploaded { get; init; }
    [JsonPropertyName("percent")] public double? Percent { get; init; }
    [JsonPropertyName("eta_seconds")] public long? EtaSeconds { get; init; }
    [JsonPropertyName("errors")] public long? Errors { get; init; }
}

/// <summary>
/// §7 acceptance metrics: "track aggregate working set/RSS, CPU, disk latency, upload throughput,
/// scanner duration, cache size and engine maintenance spikes".
/// </summary>
public sealed class ResourceSample
{
    [JsonPropertyName("profile")] public string? Profile { get; init; }
    [JsonPropertyName("throttle_level")] public int ThrottleLevel { get; init; }
    [JsonPropertyName("worker_working_set_bytes")] public long? WorkerWorkingSetBytes { get; init; }
    [JsonPropertyName("worker_peak_memory_bytes")] public long? WorkerPeakMemoryBytes { get; init; }
    [JsonPropertyName("worker_cpu_percent")] public double? WorkerCpuPercent { get; init; }
    [JsonPropertyName("system_cpu_percent")] public double? SystemCpuPercent { get; init; }
    [JsonPropertyName("disk_latency_ms")] public double? DiskLatencyMs { get; init; }
    [JsonPropertyName("available_memory_mb")] public double? AvailableMemoryMb { get; init; }
    [JsonPropertyName("upload_bytes_per_second")] public double? UploadBytesPerSecond { get; init; }
    [JsonPropertyName("scanner_duration_ms")] public long? ScannerDurationMs { get; init; }
    [JsonPropertyName("cache_size_bytes")] public long? CacheSizeBytes { get; init; }
    [JsonPropertyName("protected_app_active")] public bool? ProtectedAppActive { get; init; }
    [JsonPropertyName("agent_core_working_set_bytes")] public long? AgentCoreWorkingSetBytes { get; init; }
    [JsonPropertyName("maintenance")] public bool Maintenance { get; init; }
}

public sealed class JobResultSummary
{
    [JsonPropertyName("engine")] public string? Engine { get; init; }
    [JsonPropertyName("engine_version")] public string? EngineVersion { get; init; }
    [JsonPropertyName("engine_snapshot_ids")] public IReadOnlyList<SnapshotRef> EngineSnapshots { get; init; } = [];
    [JsonPropertyName("consistency")] public string? Consistency { get; init; }
    [JsonPropertyName("verification_state")] public string? VerificationState { get; init; }
    [JsonPropertyName("verify_files_percent")] public double? VerifyFilesPercent { get; init; }
    [JsonPropertyName("source_time")] public DateTimeOffset? SourceTime { get; init; }
    [JsonPropertyName("files_new")] public long? FilesNew { get; init; }
    [JsonPropertyName("files_changed")] public long? FilesChanged { get; init; }
    [JsonPropertyName("files_unchanged")] public long? FilesUnchanged { get; init; }
    [JsonPropertyName("source_bytes")] public long? SourceBytes { get; init; }
    [JsonPropertyName("backup_mode")] public string? BackupMode { get; init; }
    [JsonPropertyName("bytes_read")] public long? BytesRead { get; init; }
    [JsonPropertyName("bytes_uploaded")] public long? BytesUploaded { get; init; }
    [JsonPropertyName("errors")] public long? Errors { get; init; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; init; }
    [JsonPropertyName("manifest_entries")] public long? ManifestEntries { get; init; }
    [JsonPropertyName("restored_files")] public long? RestoredFiles { get; init; }
    [JsonPropertyName("restored_bytes")] public long? RestoredBytes { get; init; }
    [JsonPropertyName("restore_target")] public string? RestoreTarget { get; init; }
}

/// <summary>One engine snapshot per backed-up source root.</summary>
public sealed class SnapshotRef
{
    [JsonPropertyName("source_path")] public required string SourcePath { get; init; }
    [JsonPropertyName("snapshot_id")] public required string SnapshotId { get; init; }

    /// <summary>Engine root object/tree id (Kopia root object id, Restic tree id) when known.</summary>
    [JsonPropertyName("root_object_id")] public string? RootObjectId { get; init; }
    [JsonPropertyName("files")] public long? Files { get; init; }
    [JsonPropertyName("bytes")] public long? Bytes { get; init; }
    [JsonPropertyName("errors")] public long? Errors { get; init; }
}

/// <summary>One line (NDJSON) of the per-recovery-point manifest uploaded for safe portal browsing.</summary>
public sealed class ManifestEntry
{
    [JsonPropertyName("root")] public required string Root { get; init; }

    /// <summary>Path relative to <see cref="Root"/>, forward-slash separated, never absolute.</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("mtime")] public DateTimeOffset ModifiedAt { get; init; }
}

// ---------------------------------------------------------------- Session renewal (§4, §8, §12)

public sealed class SessionRenewRequest
{
    [JsonPropertyName("endpoint_id")] public required string EndpointId { get; init; }
    [JsonPropertyName("nonce")] public required string Nonce { get; init; }
    [JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Base64 ECDSA-P256 signature (device key) over <see cref="SessionProof.BuildPayload"/>.</summary>
    [JsonPropertyName("proof")] public required string Proof { get; init; }
}

public sealed class SessionRenewResponse
{
    [JsonPropertyName("credential")] public string? Credential { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
}

public static class SessionProof
{
    /// <summary>Canonical payload both sides sign/verify. Changing it is a protocol break.</summary>
    public static string BuildPayload(string endpointId, string nonce, DateTimeOffset timestamp) =>
        $"cloudorc-session-renew|v1|{endpointId}|{nonce}|{timestamp.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";
}

public sealed class CertificateRenewRequest
{
    [JsonPropertyName("endpoint_id")] public required string EndpointId { get; init; }
    [JsonPropertyName("csr_pem")] public required string CsrPem { get; init; }
    [JsonPropertyName("current_thumbprint")] public string? CurrentThumbprint { get; init; }
    [JsonPropertyName("nonce")] public required string Nonce { get; init; }
    [JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Base64 DER ECDSA-SHA256 (device key) over <see cref="CertificateProof.BuildPayload"/>.</summary>
    [JsonPropertyName("proof")] public required string Proof { get; init; }
}

public sealed class CertificateRenewResponse
{
    [JsonPropertyName("client_certificate_pem")] public string? ClientCertificatePem { get; init; }
}

public static class CertificateProof
{
    public static string BuildPayload(string endpointId, string nonce, DateTimeOffset timestamp, string csrSha256Hex) =>
        $"cloudorc-cert-renew|v1|{endpointId}|{nonce}|{timestamp.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}|{csrSha256Hex.ToLowerInvariant()}";
}

// ---------------------------------------------------------------- Activation-code enrollment (§4, §12)

public sealed class ActivationEnrollRequest
{
    [JsonPropertyName("invitation_code")] public required string InvitationCode { get; init; }
    [JsonPropertyName("endpoint_binding")] public required EndpointBinding EndpointBinding { get; init; }
    [JsonPropertyName("agent_version")] public required string AgentVersion { get; init; }
}

/// <summary>Machine identity proof material for tenant/endpoint binding and duplicate/reimage handling (§4).</summary>
public sealed class EndpointBinding
{
    [JsonPropertyName("machine_id")] public required string MachineId { get; init; }
    [JsonPropertyName("machine_name")] public required string MachineName { get; init; }

    /// <summary>Random id generated once per install - changes on reimage while machine_id may not.</summary>
    [JsonPropertyName("installation_id")] public required string InstallationId { get; init; }
    [JsonPropertyName("os_version")] public string? OsVersion { get; init; }

    /// <summary>SubjectPublicKeyInfo (base64) of the non-exportable device key - used for session renewal proof.</summary>
    [JsonPropertyName("device_public_key")] public string? DevicePublicKey { get; init; }

    /// <summary>PKCS#10 CSR (PEM) for an mTLS client certificate, when the backend issues one.</summary>
    [JsonPropertyName("csr_pem")] public string? CsrPem { get; init; }
}

// ---------------------------------------------------------------- Agent lifecycle events

public sealed class AgentLifecycleEvent
{
    [JsonPropertyName("event_id")] public required string EventId { get; init; }
    [JsonPropertyName("event_type")] public required string EventType { get; init; }
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = BackupSchema.Version;
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; init; }
    [JsonPropertyName("endpoint_id")] public required string EndpointId { get; init; }
    [JsonPropertyName("occurred_at")] public required DateTimeOffset OccurredAt { get; init; }
    [JsonPropertyName("data")] public IReadOnlyDictionary<string, string?> Data { get; init; } = new Dictionary<string, string?>();
}
