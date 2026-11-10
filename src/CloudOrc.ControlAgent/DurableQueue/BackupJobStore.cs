using Microsoft.Data.Sqlite;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.ControlAgent.DurableQueue;

/// <summary>Local durable record of one job (§11 backup_job / restore_job, agent side).</summary>
public sealed class BackupJobRecord
{
    public required string JobId { get; init; }
    public required string Kind { get; init; }
    public string? PolicyId { get; init; }
    public string? PolicyVersionId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string CorrelationId { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public string? LeaseId { get; init; }
    public long? FencingToken { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public string? RepositoryId { get; init; }
    public string? Engine { get; init; }

    /// <summary>Non-secret job parameters (restore selection, verify target...). Never a grant.</summary>
    public string? SpecJson { get; init; }
    public string? ReasonCode { get; init; }
    public string? Message { get; init; }
    public string? ResultJson { get; init; }
    public DateTimeOffset? ScheduledSlot { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long LastSequence { get; init; }
    public bool CancelRequested { get; init; }
    public long? EstimatedBytes { get; init; }
}

/// <summary>A state transition plus the event it emits, applied atomically.</summary>
public sealed class JobTransition
{
    public required string State { get; init; }
    public string? ReasonCode { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public bool IncrementAttempt { get; init; }
    public bool MarkStarted { get; init; }
    public bool MarkEnded { get; init; }
    public string? ResultJson { get; init; }
    public bool ClearLease { get; init; }
}

public sealed class BackupJobStore(AgentStateDatabase database)
{
    private const string Columns =
        "job_id, kind, policy_id, policy_version_id, idempotency_key, correlation_id, state, not_before, attempt, max_attempts, " +
        "lease_id, fencing_token, lease_expires_at, repository_id, engine, spec_json, reason_code, message, result_json, " +
        "scheduled_slot, created_at, started_at, ended_at, last_sequence, cancel_requested, estimated_bytes";

    /// <summary>
    /// Creates the job unless one with the same idempotency key exists (§11 backup_job.idempotency_key).
    /// Returns the stored job and whether it was newly created.
    /// </summary>
    public (BackupJobRecord Job, bool Created) CreateIfAbsent(BackupJobRecord job) =>
        database.InTransaction((connection, transaction) =>
        {
            var existing = QuerySingle(connection, transaction, "idempotency_key = $key", ("$key", job.IdempotencyKey))
                           ?? QuerySingle(connection, transaction, "job_id = $id", ("$id", job.JobId));
            if (existing is not null)
            {
                return (existing, false);
            }

            using var insert = AgentStateDatabase.Command(connection,
                """
                INSERT INTO backup_jobs(job_id, kind, policy_id, policy_version_id, idempotency_key, correlation_id, state, not_before,
                    attempt, max_attempts, repository_id, engine, spec_json, scheduled_slot, created_at, estimated_bytes)
                VALUES ($id, $kind, $pid, $pver, $key, $corr, $state, $nb, 0, $max, $repo, $engine, $spec, $slot, $created, $est);
                """,
                transaction,
                ("$id", job.JobId), ("$kind", job.Kind), ("$pid", job.PolicyId), ("$pver", job.PolicyVersionId),
                ("$key", job.IdempotencyKey), ("$corr", job.CorrelationId), ("$state", job.State),
                ("$nb", AgentStateDatabase.Timestamp(job.NotBefore)), ("$max", job.MaxAttempts), ("$repo", job.RepositoryId),
                ("$engine", job.Engine), ("$spec", job.SpecJson),
                ("$slot", job.ScheduledSlot is { } s ? AgentStateDatabase.Timestamp(s) : null),
                ("$created", AgentStateDatabase.Timestamp(job.CreatedAt)), ("$est", job.EstimatedBytes));
            insert.ExecuteNonQuery();
            return (QuerySingle(connection, transaction, "job_id = $id", ("$id", job.JobId))!, true);
        });

    public BackupJobRecord? Get(string jobId) =>
        database.Run(connection => QuerySingle(connection, null, "job_id = $id", ("$id", jobId)));

    /// <summary>Next job whose time has come, oldest first. One job at a time keeps resource use bounded (§7).</summary>
    public BackupJobRecord? NextRunnable(DateTimeOffset now) =>
        database.Run(connection => QuerySingle(connection, null,
            "state IN ('QUEUED','RETRYING') AND not_before <= $now ORDER BY CASE kind WHEN 'restore' THEN 0 ELSE 1 END, not_before",
            ("$now", AgentStateDatabase.Timestamp(now))));

    public DateTimeOffset? EarliestPendingNotBefore() =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "SELECT MIN(not_before) FROM backup_jobs WHERE state IN ('QUEUED','RETRYING');");
            return AgentStateDatabase.ParseNullableTimestamp(command.ExecuteScalar());
        });

    /// <summary>Jobs that were mid-flight when the agent stopped - reconciled at startup (§15 "Job reconciles").</summary>
    public IReadOnlyList<BackupJobRecord> GetInterrupted() =>
        database.Run(connection => QueryMany(connection,
            "state IN ('SNAPSHOTTING','SCANNING','TRANSFERRING','VERIFYING','RESTORING','MAINTAINING') ORDER BY created_at"));

    public IReadOnlyList<BackupJobRecord> GetRecent(int limit) =>
        database.Run(connection => QueryMany(connection, "1 = 1 ORDER BY created_at DESC LIMIT " + Math.Clamp(limit, 1, 500)));

    public BackupJobRecord? LatestTerminalForPolicy(string policyId, string state) =>
        database.Run(connection => QuerySingle(connection, null,
            "policy_id = $pid AND state = $state ORDER BY ended_at DESC", ("$pid", policyId), ("$state", state)));

    /// <summary>
    /// Applies a state transition and, in the SAME transaction, allocates the next event sequence
    /// number and lets the caller write the resulting outbox row - so state and the event that
    /// announces it can never diverge, even across a crash.
    /// </summary>
    public BackupJobRecord Transition(string jobId, JobTransition transition, DateTimeOffset now,
        Action<SqliteConnection, SqliteTransaction, BackupJobRecord, long>? writeEvent = null) =>
        database.InTransaction((connection, transaction) =>
        {
            using (var update = AgentStateDatabase.Command(connection,
                       """
                       UPDATE backup_jobs SET
                           state = $state,
                           reason_code = $reason,
                           message = $message,
                           not_before = COALESCE($nb, not_before),
                           attempt = attempt + $inc,
                           started_at = CASE WHEN $started = 1 THEN $now ELSE started_at END,
                           ended_at = CASE WHEN $ended = 1 THEN $now ELSE ended_at END,
                           result_json = COALESCE($result, result_json),
                           lease_id = CASE WHEN $clear = 1 THEN NULL ELSE lease_id END,
                           fencing_token = CASE WHEN $clear = 1 THEN NULL ELSE fencing_token END,
                           lease_expires_at = CASE WHEN $clear = 1 THEN NULL ELSE lease_expires_at END,
                           last_sequence = last_sequence + $bump
                       WHERE job_id = $id AND state NOT IN ('SUCCEEDED','PARTIAL','FAILED','CANCELLED');
                       """,
                       transaction,
                       ("$state", transition.State), ("$reason", transition.ReasonCode), ("$message", transition.Message),
                       ("$nb", transition.NotBefore is { } nb ? AgentStateDatabase.Timestamp(nb) : null),
                       ("$inc", transition.IncrementAttempt ? 1 : 0), ("$started", transition.MarkStarted ? 1 : 0),
                       ("$ended", transition.MarkEnded ? 1 : 0), ("$now", AgentStateDatabase.Timestamp(now)),
                       ("$result", transition.ResultJson), ("$clear", transition.ClearLease ? 1 : 0),
                       ("$bump", writeEvent is null ? 0 : 1), ("$id", jobId)))
            {
                if (update.ExecuteNonQuery() != 1)
                {
                    // Terminal is final: a late writer (stale runner, superseded job) must not resurrect it.
                    return QuerySingle(connection, transaction, "job_id = $id", ("$id", jobId))
                           ?? throw new InvalidOperationException($"Job {jobId} does not exist.");
                }
            }

            var job = QuerySingle(connection, transaction, "job_id = $id", ("$id", jobId))!;
            writeEvent?.Invoke(connection, transaction, job, job.LastSequence);
            return job;
        });

    /// <summary>Allocates a sequence number for an event that does not change state (progress).</summary>
    public long AppendEvent(string jobId, Action<SqliteConnection, SqliteTransaction, BackupJobRecord, long> writeEvent) =>
        database.InTransaction((connection, transaction) =>
        {
            using (var bump = AgentStateDatabase.Command(connection,
                       "UPDATE backup_jobs SET last_sequence = last_sequence + 1 WHERE job_id = $id;", transaction, ("$id", jobId)))
            {
                if (bump.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException($"Job {jobId} does not exist.");
                }
            }

            var job = QuerySingle(connection, transaction, "job_id = $id", ("$id", jobId))!;
            writeEvent(connection, transaction, job, job.LastSequence);
            return job.LastSequence;
        });

    public void SetLease(string jobId, AdmissionLease lease) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "UPDATE backup_jobs SET lease_id = $lease, fencing_token = $fence, lease_expires_at = $exp WHERE job_id = $id;",
                null,
                ("$lease", lease.LeaseId), ("$fence", lease.FencingToken),
                ("$exp", AgentStateDatabase.Timestamp(lease.ExpiresAt)), ("$id", jobId));
            command.ExecuteNonQuery();
        });

    public void SetEstimatedBytes(string jobId, long bytes) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "UPDATE backup_jobs SET estimated_bytes = $b WHERE job_id = $id;", null, ("$b", bytes), ("$id", jobId));
            command.ExecuteNonQuery();
        });

    /// <summary>Marks a job for cancellation. Returns false if unknown or already terminal.</summary>
    public bool RequestCancel(string jobId) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "UPDATE backup_jobs SET cancel_requested = 1 WHERE job_id = $id AND state NOT IN ('SUCCEEDED','PARTIAL','FAILED','CANCELLED');",
                null, ("$id", jobId));
            return command.ExecuteNonQuery() == 1;
        });

    public void SaveCheckpoint(string jobId, string stage, string? engineCheckpointRef, string? sourceSnapshotRef, string? lastProgressJson, DateTimeOffset now) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                """
                INSERT INTO backup_checkpoints(job_id, stage, engine_checkpoint_ref, source_snapshot_ref, last_progress, updated_at)
                VALUES ($id, $stage, $eng, $src, $prog, $now)
                ON CONFLICT(job_id) DO UPDATE SET stage = excluded.stage,
                    engine_checkpoint_ref = COALESCE(excluded.engine_checkpoint_ref, backup_checkpoints.engine_checkpoint_ref),
                    source_snapshot_ref = COALESCE(excluded.source_snapshot_ref, backup_checkpoints.source_snapshot_ref),
                    last_progress = COALESCE(excluded.last_progress, backup_checkpoints.last_progress),
                    updated_at = excluded.updated_at;
                """,
                null,
                ("$id", jobId), ("$stage", stage), ("$eng", engineCheckpointRef), ("$src", sourceSnapshotRef),
                ("$prog", lastProgressJson), ("$now", AgentStateDatabase.Timestamp(now)));
            command.ExecuteNonQuery();
        });

    public (string Stage, string? EngineCheckpointRef)? GetCheckpoint(string jobId) =>
        database.Run<(string, string?)?>(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "SELECT stage, engine_checkpoint_ref FROM backup_checkpoints WHERE job_id = $id;", null, ("$id", jobId));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
        });

    public int PruneTerminalBefore(DateTimeOffset cutoff) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "DELETE FROM backup_jobs WHERE state IN ('SUCCEEDED','PARTIAL','FAILED','CANCELLED') AND ended_at < $cutoff;",
                null, ("$cutoff", AgentStateDatabase.Timestamp(cutoff)));
            return command.ExecuteNonQuery();
        });

    // ------------------------------------------------------------------ mapping

    private static BackupJobRecord? QuerySingle(SqliteConnection connection, SqliteTransaction? transaction, string where, params (string, object?)[] parameters)
    {
        using var command = AgentStateDatabase.Command(connection, $"SELECT {Columns} FROM backup_jobs WHERE {where} LIMIT 1;", transaction, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static IReadOnlyList<BackupJobRecord> QueryMany(SqliteConnection connection, string where)
    {
        using var command = AgentStateDatabase.Command(connection, $"SELECT {Columns} FROM backup_jobs WHERE {where};");
        using var reader = command.ExecuteReader();
        var list = new List<BackupJobRecord>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    private static BackupJobRecord Map(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

        return new BackupJobRecord
        {
            JobId = r.GetString(0),
            Kind = r.GetString(1),
            PolicyId = S(2),
            PolicyVersionId = S(3),
            IdempotencyKey = r.GetString(4),
            CorrelationId = r.GetString(5),
            State = r.GetString(6),
            NotBefore = AgentStateDatabase.ParseTimestamp(r.GetString(7)),
            Attempt = (int)r.GetInt64(8),
            MaxAttempts = (int)r.GetInt64(9),
            LeaseId = S(10),
            FencingToken = L(11),
            LeaseExpiresAt = AgentStateDatabase.ParseNullableTimestamp(S(12)),
            RepositoryId = S(13),
            Engine = S(14),
            SpecJson = S(15),
            ReasonCode = S(16),
            Message = S(17),
            ResultJson = S(18),
            ScheduledSlot = AgentStateDatabase.ParseNullableTimestamp(S(19)),
            CreatedAt = AgentStateDatabase.ParseTimestamp(r.GetString(20)),
            StartedAt = AgentStateDatabase.ParseNullableTimestamp(S(21)),
            EndedAt = AgentStateDatabase.ParseNullableTimestamp(S(22)),
            LastSequence = r.GetInt64(23),
            CancelRequested = r.GetInt64(24) == 1,
            EstimatedBytes = L(25)
        };
    }
}
