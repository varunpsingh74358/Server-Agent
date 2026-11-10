using Microsoft.Data.Sqlite;

namespace CloudOrc.ControlAgent.DurableQueue;

/// <summary>One message waiting to be delivered to the HTTPS control plane.</summary>
public sealed class OutboxItem
{
    public long Sequence { get; init; }
    public required string EventId { get; init; }

    /// <summary>"job-event", "restore-event", "manifest", "agent-event".</summary>
    public required string Kind { get; init; }

    /// <summary>Relative control-plane route, e.g. api/v1/backup/jobs/{id}/events.</summary>
    public required string Route { get; init; }

    /// <summary>JSON body, or null when <see cref="FilePath"/> carries the body (large manifests).</summary>
    public string? Payload { get; init; }
    public string? FilePath { get; init; }
    public string? ContentType { get; init; }
    public string? JobId { get; init; }

    /// <summary>Terminal events are never dropped by the size cap; progress events may be.</summary>
    public bool IsTerminal { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset NextAttemptAt { get; init; }
}

public static class OutboxKinds
{
    public const string JobEvent = "job-event";
    public const string JobProgress = "job-progress";
    public const string RestoreEvent = "restore-event";
    public const string Manifest = "manifest";
    public const string AgentEvent = "agent-event";
}

/// <summary>
/// Durable transactional outbox (§11 "Use outbox/inbox for at-least-once delivery"). Events are
/// written in the same transaction as the job state change that produced them, then delivered
/// in sequence order by <c>OutboxDispatcherService</c>. A row is deleted only after the backend
/// acknowledges it with 2xx (or 409 = already have it), so an agent restart or a network outage
/// never loses a job event; the backend de-duplicates on event_id.
/// </summary>
public sealed class OutboxStore(AgentStateDatabase database)
{
    public void Enqueue(OutboxItem item, DateTimeOffset now) =>
        database.Run(connection => Insert(connection, null, item, now));

    /// <summary>Use from inside <see cref="AgentStateDatabase.InTransaction{T}"/> to make the write atomic with a state change.</summary>
    public static void Insert(SqliteConnection connection, SqliteTransaction? transaction, OutboxItem item, DateTimeOffset now)
    {
        using var command = AgentStateDatabase.Command(connection,
            """
            INSERT OR IGNORE INTO outbox(event_id, kind, route, payload, file_path, content_type, job_id, is_terminal, created_at, attempts, next_attempt_at)
            VALUES ($event, $kind, $route, $payload, $file, $ctype, $job, $terminal, $now, 0, $now);
            """,
            transaction,
            ("$event", item.EventId), ("$kind", item.Kind), ("$route", item.Route), ("$payload", item.Payload),
            ("$file", item.FilePath), ("$ctype", item.ContentType), ("$job", item.JobId),
            ("$terminal", item.IsTerminal ? 1 : 0), ("$now", AgentStateDatabase.Timestamp(now)));
        command.ExecuteNonQuery();
    }

    /// <summary>The oldest undelivered, non-dead rows in sequence order (head-of-line first).</summary>
    public IReadOnlyList<OutboxItem> PeekHead(int limit) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                """
                SELECT seq, event_id, kind, route, payload, file_path, content_type, job_id, is_terminal, attempts, next_attempt_at
                FROM outbox WHERE dead = 0 ORDER BY seq LIMIT $limit;
                """,
                null, ("$limit", limit));
            using var reader = command.ExecuteReader();
            var items = new List<OutboxItem>();
            while (reader.Read())
            {
                items.Add(new OutboxItem
                {
                    Sequence = reader.GetInt64(0),
                    EventId = reader.GetString(1),
                    Kind = reader.GetString(2),
                    Route = reader.GetString(3),
                    Payload = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FilePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ContentType = reader.IsDBNull(6) ? null : reader.GetString(6),
                    JobId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    IsTerminal = reader.GetInt64(8) == 1,
                    Attempts = (int)reader.GetInt64(9),
                    NextAttemptAt = AgentStateDatabase.ParseTimestamp(reader.GetString(10))
                });
            }

            return items;
        });

    public void MarkDelivered(long sequence) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection, "DELETE FROM outbox WHERE seq = $seq;", null, ("$seq", sequence));
            command.ExecuteNonQuery();
        });

    public void MarkFailed(long sequence, string error, DateTimeOffset nextAttemptAt, bool dead) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "UPDATE outbox SET attempts = attempts + 1, last_error = $err, next_attempt_at = $next, dead = $dead WHERE seq = $seq;",
                null,
                ("$err", error.Length > 500 ? error[..500] : error), ("$next", AgentStateDatabase.Timestamp(nextAttemptAt)),
                ("$dead", dead ? 1 : 0), ("$seq", sequence));
            command.ExecuteNonQuery();
        });

    public long CountPending() =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection, "SELECT COUNT(*) FROM outbox WHERE dead = 0;");
            return (long)command.ExecuteScalar()!;
        });

    /// <summary>
    /// Bounded state (§13): when the backlog exceeds <paramref name="maxRows"/> after a very long
    /// outage, the oldest NON-terminal progress events are dropped first. Terminal events (and
    /// manifests) are never dropped - the backend must always learn how a job ended.
    /// </summary>
    public int EnforceCap(int maxRows) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                """
                DELETE FROM outbox WHERE seq IN (
                    SELECT seq FROM outbox WHERE is_terminal = 0 AND kind = 'job-progress'
                    ORDER BY seq LIMIT MAX(0, (SELECT COUNT(*) FROM outbox) - $max));
                """,
                null, ("$max", maxRows));
            return command.ExecuteNonQuery();
        });

    public IReadOnlyList<string> PruneDeadBefore(DateTimeOffset cutoff) =>
        database.InTransaction((connection, transaction) =>
        {
            var files = new List<string>();
            using (var select = AgentStateDatabase.Command(connection,
                       "SELECT file_path FROM outbox WHERE dead = 1 AND created_at < $cutoff AND file_path IS NOT NULL;",
                       transaction, ("$cutoff", AgentStateDatabase.Timestamp(cutoff))))
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    files.Add(reader.GetString(0));
                }
            }

            using var delete = AgentStateDatabase.Command(connection,
                "DELETE FROM outbox WHERE dead = 1 AND created_at < $cutoff;", transaction, ("$cutoff", AgentStateDatabase.Timestamp(cutoff)));
            delete.ExecuteNonQuery();
            return (IReadOnlyList<string>)files;
        });
}
