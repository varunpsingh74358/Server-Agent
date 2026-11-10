using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Protocol;

namespace CloudOrc.ControlAgent.DurableQueue;

public sealed record ScheduleState(string PolicyId, DateTimeOffset? LastSlot, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastVerifiedAt, long? LastSourceBytes);

/// <summary>
/// Persists the effective policies the backend applied (§11 backup_assignment desired state) and
/// per-policy schedule bookkeeping, so scheduling survives restarts and works while offline
/// (the job will still wait for admission before running).
/// </summary>
public sealed class BackupPolicyStore(AgentStateDatabase database)
{
    public void Upsert(BackupPolicyDocument policy, DateTimeOffset now) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                """
                INSERT INTO backup_policy(policy_id, policy_version_id, effective_hash, document, applied_at)
                VALUES ($id, $ver, $hash, $doc, $at)
                ON CONFLICT(policy_id) DO UPDATE SET
                    policy_version_id = excluded.policy_version_id,
                    effective_hash = excluded.effective_hash,
                    document = excluded.document,
                    applied_at = excluded.applied_at;
                """,
                null,
                ("$id", policy.PolicyId), ("$ver", policy.PolicyVersionId), ("$hash", policy.EffectiveHash),
                ("$doc", JsonSerializer.Serialize(policy, ProtocolJson.Options)), ("$at", AgentStateDatabase.Timestamp(now)));
            command.ExecuteNonQuery();
        });

    public bool Remove(string policyId) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection, "DELETE FROM backup_policy WHERE policy_id = $id;", null, ("$id", policyId));
            return command.ExecuteNonQuery() > 0;
        });

    public BackupPolicyDocument? Get(string policyId) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection, "SELECT document FROM backup_policy WHERE policy_id = $id;", null, ("$id", policyId));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<BackupPolicyDocument>(json, ProtocolJson.Options) : null;
        });

    public IReadOnlyList<BackupPolicyDocument> GetAll() =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection, "SELECT document FROM backup_policy ORDER BY policy_id;");
            using var reader = command.ExecuteReader();
            var list = new List<BackupPolicyDocument>();
            while (reader.Read())
            {
                var policy = JsonSerializer.Deserialize<BackupPolicyDocument>(reader.GetString(0), ProtocolJson.Options);
                if (policy is not null)
                {
                    list.Add(policy);
                }
            }

            return (IReadOnlyList<BackupPolicyDocument>)list;
        });

    public ScheduleState GetScheduleState(string policyId) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "SELECT last_slot, last_success_at, last_verified_at, last_source_bytes FROM schedule_state WHERE policy_id = $id;",
                null, ("$id", policyId));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return new ScheduleState(policyId, null, null, null, null);
            }

            return new ScheduleState(
                policyId,
                AgentStateDatabase.ParseNullableTimestamp(reader.IsDBNull(0) ? null : reader.GetString(0)),
                AgentStateDatabase.ParseNullableTimestamp(reader.IsDBNull(1) ? null : reader.GetString(1)),
                AgentStateDatabase.ParseNullableTimestamp(reader.IsDBNull(2) ? null : reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt64(3));
        });

    public void SetLastSlot(string policyId, DateTimeOffset slot) =>
        Upsert(policyId, "last_slot", AgentStateDatabase.Timestamp(slot));

    public void RecordSuccess(string policyId, DateTimeOffset at, bool verified, long? sourceBytes) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                """
                INSERT INTO schedule_state(policy_id, last_success_at, last_verified_at, last_source_bytes)
                VALUES ($id, $at, CASE WHEN $verified = 1 THEN $at ELSE NULL END, $bytes)
                ON CONFLICT(policy_id) DO UPDATE SET
                    last_success_at = excluded.last_success_at,
                    last_verified_at = CASE WHEN $verified = 1 THEN $at ELSE schedule_state.last_verified_at END,
                    last_source_bytes = COALESCE($bytes, schedule_state.last_source_bytes);
                """,
                null,
                ("$id", policyId), ("$at", AgentStateDatabase.Timestamp(at)), ("$verified", verified ? 1 : 0), ("$bytes", sourceBytes));
            command.ExecuteNonQuery();
        });

    private void Upsert(string policyId, string column, string value) =>
        database.Run(connection =>
        {
            // column is one of a fixed set chosen by this class - never caller input.
            using var command = AgentStateDatabase.Command(connection,
                $"INSERT INTO schedule_state(policy_id, {column}) VALUES ($id, $v) ON CONFLICT(policy_id) DO UPDATE SET {column} = excluded.{column};",
                null, ("$id", policyId), ("$v", value));
            command.ExecuteNonQuery();
        });
}
