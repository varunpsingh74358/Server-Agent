using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// The COMMAND envelope for typed commands. Same outer shape as <see cref="Protocol.CommandMessage"/>
/// (type/commandId/correlationId/commandType/createdAt) plus a version and a typed parameter object,
/// so the existing gateway only needs to fill <see cref="Parameters"/> with a different shape.
/// </summary>
public sealed class TypedCommandMessage<TParameters> where TParameters : class
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = Protocol.ProtocolMessageTypes.Command;

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("commandType")]
    public required string CommandType { get; init; }

    /// <summary>Command schema version (§10 "versioned"). Agent rejects unknown majors.</summary>
    [JsonPropertyName("commandVersion")]
    public string CommandVersion { get; init; } = BackupSchema.Version;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Who requested it (portal user / scheduler), echoed into audit events.</summary>
    [JsonPropertyName("actor")]
    public string? Actor { get; init; }

    [JsonPropertyName("parameters")]
    public TParameters? Parameters { get; init; }

    /// <summary>When the backend issued (and signed) the command - bounds replay of a captured command.</summary>
    [JsonPropertyName("issuedAt")]
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>Base64 DER ECDSA-SHA256 signature over <see cref="CommandSigning.BuildPayload"/> (§10 "signed").</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }
}

/// <summary>
/// Canonical signed payload of a typed command (§10 "All commands typed, signed/authenticated").
/// The backend signs it with its command-signing key; the agent verifies it with the public key it
/// received at enrollment. The parameters are bound by the SHA-256 of their exact JSON text as sent.
/// Changing this format is a protocol break.
/// </summary>
public static class CommandSigning
{
    public static string BuildPayload(string commandId, string commandType, string commandVersion, DateTimeOffset issuedAt, string parametersSha256Hex) =>
        $"cloudorc-command|v1|{commandId}|{commandType}|{commandVersion}|{issuedAt.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}|{parametersSha256Hex.ToLowerInvariant()}";
}

public sealed class PolicyApplyParameters
{
    [JsonPropertyName("policy")]
    public BackupPolicyDocument? Policy { get; init; }
}

public sealed class PolicyRemoveParameters
{
    [JsonPropertyName("policyId")]
    public string? PolicyId { get; init; }
}

public sealed class BackupRunParameters
{
    /// <summary>Backend-issued job id - the agent uses it as the job's idempotency key.</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("policyId")]
    public string? PolicyId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class JobCancelParameters
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }
}

public sealed class VerifyParameters
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("repositoryId")]
    public string? RepositoryId { get; init; }

    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    [JsonPropertyName("engineSnapshotId")]
    public string? EngineSnapshotId { get; init; }

    [JsonPropertyName("verifyFilesPercent")]
    public double VerifyFilesPercent { get; init; } = 5;
}

public sealed class MaintenanceParameters
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("repositoryId")]
    public string? RepositoryId { get; init; }

    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    /// <summary>Policy whose retention to enforce (Restic forget) - optional for Kopia.</summary>
    [JsonPropertyName("policyId")]
    public string? PolicyId { get; init; }

    /// <summary>Full (data-deleting) maintenance vs quick index maintenance.</summary>
    [JsonPropertyName("full")]
    public bool Full { get; init; } = true;
}

/// <summary>
/// §5/§10: restore is separately authorized - the backend only sends this after approval.
/// The agent still re-validates the target and every selected path locally.
/// </summary>
public sealed class RestoreExecuteParameters
{
    [JsonPropertyName("restoreJobId")]
    public string? RestoreJobId { get; init; }

    [JsonPropertyName("recoveryPointId")]
    public string? RecoveryPointId { get; init; }

    [JsonPropertyName("repositoryId")]
    public string? RepositoryId { get; init; }

    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    [JsonPropertyName("engineSnapshotId")]
    public string? EngineSnapshotId { get; init; }

    /// <summary>The source root path the snapshot was taken of (e.g. D:\Tally\Data).</summary>
    [JsonPropertyName("sourceRoot")]
    public string? SourceRoot { get; init; }

    /// <summary>Paths relative to <see cref="SourceRoot"/>; empty = whole snapshot.</summary>
    [JsonPropertyName("selection")]
    public IReadOnlyList<string> Selection { get; init; } = [];

    /// <summary>"original" (restore over SourceRoot) or "alternate" (TargetPath).</summary>
    [JsonPropertyName("targetKind")]
    public string TargetKind { get; init; } = RestoreTargetKinds.Alternate;

    [JsonPropertyName("targetPath")]
    public string? TargetPath { get; init; }

    /// <summary>"overwrite", "rename" (restore beside, never touching existing files) or "skip-existing".</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = RestoreModes.Rename;

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("approvedBy")]
    public string? ApprovedBy { get; init; }
}

public static class RestoreTargetKinds
{
    public const string Original = "original";
    public const string Alternate = "alternate";
}

public static class RestoreModes
{
    public const string Overwrite = "overwrite";
    public const string Rename = "rename";
    public const string SkipExisting = "skip-existing";
}

/// <summary>§13 signed staged update. Cohort selection happens server-side; the agent verifies.</summary>
public sealed class AgentUpdateParameters
{
    [JsonPropertyName("updateId")]
    public string? UpdateId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>HTTPS URL of the signed installer.</summary>
    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("cohort")]
    public string? Cohort { get; init; }
}

/// <summary>Result payload returned in COMMAND_RESULT.output[0] for typed commands (JSON).</summary>
public sealed class TypedCommandAcceptance
{
    [JsonPropertyName("accepted")]
    public required bool Accepted { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
