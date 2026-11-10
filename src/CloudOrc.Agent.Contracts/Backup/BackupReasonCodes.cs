namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// Machine-readable reason codes carried on job events (§12 "reason_code"). Stable strings:
/// the portal maps them to readable messages; never put secrets or raw paths in them.
/// </summary>
public static class BackupReasonCodes
{
    // Admission / authorization
    public const string AdmissionDeferred = "ADMISSION_DEFERRED";
    public const string AdmissionDenied = "ADMISSION_DENIED";
    public const string ControlPlaneUnreachable = "CONTROL_PLANE_UNREACHABLE";
    public const string AuthorizationRejected = "AUTHORIZATION_REJECTED";
    public const string AuthorizationRevoked = "AUTHORIZATION_REVOKED";
    public const string LeaseLost = "LEASE_LOST";

    // Storage
    public const string StorageGrantFailed = "STORAGE_GRANT_FAILED";
    public const string StorageCredentialExpired = "STORAGE_CREDENTIAL_EXPIRED";
    public const string StorageFull = "STORAGE_FULL";
    public const string StorageThrottled = "STORAGE_THROTTLED";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";
    public const string RepositoryLocked = "REPOSITORY_LOCKED";
    public const string RepositoryNotInitialized = "REPOSITORY_NOT_INITIALIZED";
    public const string RepositoryCorrupt = "REPOSITORY_CORRUPT";
    public const string RepositoryPasswordInvalid = "REPOSITORY_PASSWORD_INVALID";

    // Source / consistency
    public const string SnapshotFailed = "SNAPSHOT_FAILED";
    public const string SnapshotStorageInsufficient = "SNAPSHOT_STORAGE_INSUFFICIENT";
    public const string SnapshotLifetimeExceeded = "SNAPSHOT_LIFETIME_EXCEEDED";
    public const string ApplicationNotQuiesced = "APPLICATION_NOT_QUIESCED";
    public const string SourceNotFound = "SOURCE_NOT_FOUND";
    public const string SourceReadErrors = "SOURCE_READ_ERRORS";
    public const string LocalDiskError = "LOCAL_DISK_ERROR";

    // Engine / worker
    public const string EngineBinaryMissing = "ENGINE_BINARY_MISSING";
    public const string EngineBinaryTampered = "ENGINE_BINARY_TAMPERED";
    public const string EngineFailed = "ENGINE_FAILED";
    public const string WorkerCrashed = "WORKER_CRASHED";
    public const string AgentRestarted = "AGENT_RESTARTED";
    public const string RetriesExhausted = "RETRIES_EXHAUSTED";
    public const string Cancelled = "CANCELLED";
    public const string PolicyInvalid = "POLICY_INVALID";
    public const string PolicyNotFound = "POLICY_NOT_FOUND";
    public const string Superseded = "SUPERSEDED";
    public const string UnsupportedProvider = "UNSUPPORTED_PROVIDER";
    public const string ResourceBudgetExceeded = "RESOURCE_BUDGET_EXCEEDED";
    public const string JobTimeout = "JOB_TIMEOUT";

    // Verification
    public const string VerificationFailed = "VERIFICATION_FAILED";

    // Restore
    public const string RestoreTargetInvalid = "RESTORE_TARGET_INVALID";
    public const string RestorePathUnsafe = "RESTORE_PATH_UNSAFE";
    public const string RestoreVerificationFailed = "RESTORE_VERIFICATION_FAILED";

    /// <summary>Reason codes after which an automatic retry is reasonable.</summary>
    public static bool IsTransient(string? reasonCode) => reasonCode is
        ControlPlaneUnreachable or StorageThrottled or StorageUnavailable or StorageCredentialExpired or
        RepositoryLocked or WorkerCrashed or AgentRestarted or LeaseLost or EngineFailed or SnapshotFailed or
        // A retry resumes: Kopia keeps checkpointed content, Restic re-uses already uploaded blobs.
        SnapshotLifetimeExceeded or JobTimeout;
}
