namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// Typed, allow-listed backup/restore/agent-lifecycle command types carried by the existing
/// COMMAND envelope over the existing agent WebSocket (CO-DEV-BKP-001 §10: "All commands
/// typed, signed/authenticated, versioned, allow-listed, idempotent and audited; no
/// arbitrary shell by default").
///
/// These deliberately do NOT flow through the sequential PowerShell command queue: a backup
/// can run for hours, and must never block (or be blocked by) the short-lived commands that
/// queue exists for. They are routed to the backup command dispatcher instead.
/// </summary>
public static class BackupCommandTypes
{
    /// <summary>Stores/replaces the effective backup policy for this endpoint.</summary>
    public const string PolicyApply = "backup.policy.apply";

    /// <summary>Removes a previously applied policy (stops its schedule).</summary>
    public const string PolicyRemove = "backup.policy.remove";

    /// <summary>On-demand backup for an applied policy (job id is backend-issued).</summary>
    public const string Run = "backup.run";

    /// <summary>Cancels a queued or running job of any kind.</summary>
    public const string Cancel = "backup.cancel";

    /// <summary>Verifies an existing recovery point (engine-native verify).</summary>
    public const string Verify = "backup.verify";

    /// <summary>Repository maintenance (retention prune / compaction), throttled.</summary>
    public const string Maintenance = "backup.maintenance";

    /// <summary>Restores an approved selection from a recovery point.</summary>
    public const string RestoreExecute = "restore.execute";

    /// <summary>Signed agent self-update (CO-DEV-BKP-001 §13).</summary>
    public const string AgentUpdate = "agent.update";

    /// <summary>Asks the agent to renew its session credential using its device key.</summary>
    public const string AgentCredentialRenew = "agent.credential.renew";

    /// <summary>Asks the agent to rotate its mTLS client certificate now.</summary>
    public const string AgentCertificateRenew = "agent.certificate.renew";

    /// <summary>
    /// Lists one folder level on the endpoint for the portal's folder picker (Protection Plan
    /// "what to back up", screens F02/W01). Read-only; limited to the signed allowedRoots.
    /// </summary>
    public const string DiscoveryBrowse = "discovery.browse";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PolicyApply, PolicyRemove, Run, Cancel, Verify, Maintenance, RestoreExecute, AgentUpdate, AgentCredentialRenew, AgentCertificateRenew,
        DiscoveryBrowse
    };

    public static bool IsTyped(string? commandType) => commandType is not null && All.Contains(commandType);
}

/// <summary>Capability strings advertised in HELLO so the backend knows what this agent supports.</summary>
public static class AgentCapabilities
{
    public const string BackupV1 = "backup.v1";
    public const string RestoreV1 = "restore.v1";
    public const string ActivationCodeV1 = "activation-code.v1";
    public const string SessionRenewV1 = "session-renew.v1";
    public const string SignedUpdateV1 = "signed-update.v1";
    public const string DurableEventsV1 = "durable-events.v1";
    public const string DiscoveryV1 = "discovery.v1";
}

/// <summary>Version of every backup event/command schema produced by this agent build.</summary>
public static class BackupSchema
{
    public const string Version = "1.0";

    /// <summary>Agent protocol version advertised in HELLO.</summary>
    public const string ProtocolVersion = "2";
}
