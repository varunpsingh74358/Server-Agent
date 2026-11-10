namespace CloudOrc.ControlAgent.DurableQueue;

public enum InboxDisposition
{
    /// <summary>First time this commandId has been seen - execute it.</summary>
    New,

    /// <summary>Already received and still being handled - do not execute again.</summary>
    InProgress,

    /// <summary>Already handled - replay the stored result instead of executing again.</summary>
    Completed
}

public sealed record InboxEntry(InboxDisposition Disposition, string? ResultJson);

/// <summary>
/// Durable command inbox (§11 "outbox/inbox ... deduplication"; §3 "idempotent ACK/result";
/// §15 "duplicate commands idempotent"). A typed command is recorded BEFORE it is acted on,
/// so a backend redelivery after a reconnect or an agent restart is recognized, and the
/// original result is re-sent instead of the command running twice.
/// </summary>
public sealed class CommandInboxStore(AgentStateDatabase database)
{
    public const string StateReceived = "RECEIVED";
    public const string StateCompleted = "COMPLETED";

    public InboxEntry Begin(string commandId, string commandType, string? correlationId, string? actor, DateTimeOffset now) =>
        database.InTransaction((connection, transaction) =>
        {
            using (var select = AgentStateDatabase.Command(connection,
                       "SELECT state, result_json FROM command_inbox WHERE command_id = $id;", transaction, ("$id", commandId)))
            using (var reader = select.ExecuteReader())
            {
                if (reader.Read())
                {
                    var state = reader.GetString(0);
                    var result = reader.IsDBNull(1) ? null : reader.GetString(1);
                    return state == StateCompleted
                        ? new InboxEntry(InboxDisposition.Completed, result)
                        : new InboxEntry(InboxDisposition.InProgress, null);
                }
            }

            using var insert = AgentStateDatabase.Command(connection,
                """
                INSERT INTO command_inbox(command_id, command_type, correlation_id, actor, received_at, state)
                VALUES ($id, $type, $corr, $actor, $at, $state);
                """,
                transaction,
                ("$id", commandId), ("$type", commandType), ("$corr", correlationId), ("$actor", actor),
                ("$at", AgentStateDatabase.Timestamp(now)), ("$state", StateReceived));
            insert.ExecuteNonQuery();
            return new InboxEntry(InboxDisposition.New, null);
        });

    public void Complete(string commandId, string resultJson, DateTimeOffset now) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "UPDATE command_inbox SET state = $state, result_json = $result, completed_at = $at WHERE command_id = $id;",
                null,
                ("$state", StateCompleted), ("$result", resultJson), ("$at", AgentStateDatabase.Timestamp(now)), ("$id", commandId));
            command.ExecuteNonQuery();
        });

    /// <summary>
    /// A RECEIVED row left behind by a crash between Begin and Complete would make every
    /// redelivery look "in progress" forever. On startup those are marked completed with a
    /// failure result so a redelivery gets a definite answer.
    /// </summary>
    public int AbandonInFlight(string resultJson, DateTimeOffset now) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "UPDATE command_inbox SET state = $done, result_json = $result, completed_at = $at WHERE state = $received;",
                null,
                ("$done", StateCompleted), ("$result", resultJson), ("$at", AgentStateDatabase.Timestamp(now)), ("$received", StateReceived));
            return command.ExecuteNonQuery();
        });

    public int PruneCompletedBefore(DateTimeOffset cutoff) =>
        database.Run(connection =>
        {
            using var command = AgentStateDatabase.Command(connection,
                "DELETE FROM command_inbox WHERE state = $done AND completed_at < $cutoff;",
                null, ("$done", StateCompleted), ("$cutoff", AgentStateDatabase.Timestamp(cutoff)));
            return command.ExecuteNonQuery();
        });
}
