using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.DurableQueue;

/// <summary>
/// The agent's local durable state (CO-DEV-BKP-001 §3 "local durable queue", §8 "Persist local
/// job intent, last durable stage, engine/repository identifiers and checkpoint metadata",
/// §11 "outbox/inbox for at-least-once delivery and deduplication", §13 "Secure local SQLite or
/// equivalent bounded durable state with OS-protected credentials and crash-safe writes").
///
/// - Crash-safe: WAL journal + synchronous=FULL, every multi-row change in one transaction.
/// - OS-protected: the state folder's ACL is reduced to SYSTEM + Administrators (no inheritance).
/// - Bounded: <see cref="DurableStateRetentionService"/> prunes old rows and caps the outbox.
/// - Never holds secrets: storage grants / repository passwords live only in memory.
///
/// One connection, serialized by a lock: the agent's write rate is tiny (a few rows per
/// second at most) and SQLite is single-writer anyway, so this is simpler and safer than pooling.
/// </summary>
public sealed class AgentStateDatabase : IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private readonly Lock _sync = new();
    private readonly SqliteConnection _connection;
    private readonly ILogger<AgentStateDatabase> _logger;

    public AgentStateDatabase(string databasePath, ILogger<AgentStateDatabase> logger, bool hardenDirectoryAcl = true)
    {
        _logger = logger;
        DatabasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(directory);
        if (hardenDirectoryAcl && OperatingSystem.IsWindows())
        {
            TryHardenDirectoryAcl(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };

        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA foreign_keys=ON;");
        Execute("PRAGMA busy_timeout=5000;");

        Migrate();
    }

    public string DatabasePath { get; }

    /// <summary>Runs <paramref name="action"/> with exclusive access to the connection.</summary>
    public T Run<T>(Func<SqliteConnection, T> action)
    {
        lock (_sync)
        {
            return action(_connection);
        }
    }

    public void Run(Action<SqliteConnection> action)
    {
        lock (_sync)
        {
            action(_connection);
        }
    }

    /// <summary>Runs <paramref name="action"/> inside one transaction - all of it commits, or none of it.</summary>
    public T InTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            var result = action(_connection, transaction);
            transaction.Commit();
            return result;
        }
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> action) =>
        InTransaction<object?>((c, t) => { action(c, t); return null; });

    public void Dispose()
    {
        lock (_sync)
        {
            _connection.Dispose();
        }
    }

    // ------------------------------------------------------------------ helpers shared by stores

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? ParseNullableTimestamp(object? value) =>
        value is string s && !string.IsNullOrEmpty(s) ? ParseTimestamp(s) : null;

    public static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Migrate()
    {
        using var versionCommand = _connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Agent state database schema version {version} is newer than this agent build supports ({CurrentSchemaVersion}). " +
                "A downgrade is not supported - reinstall the newer agent version.");
        }

        if (version < 1)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SchemaV1;
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
            transaction.Commit();
            _logger.LogInformation("Agent state database created at {Path} (schema v1).", DatabasePath);
        }
    }

    [SupportedOSPlatform("windows")]
    private void TryHardenDirectoryAcl(string directory)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or PrivilegeNotHeldException)
        {
            // Not fatal (e.g. a developer running unelevated) - the parent ProgramData ACL still applies.
            _logger.LogWarning(ex, "Could not restrict the ACL of {Directory} to SYSTEM and Administrators.", directory);
        }
    }

    private const string SchemaV1 = """
        CREATE TABLE command_inbox(
            command_id      TEXT PRIMARY KEY,
            command_type    TEXT NOT NULL,
            correlation_id  TEXT,
            actor           TEXT,
            received_at     TEXT NOT NULL,
            state           TEXT NOT NULL,
            result_json     TEXT,
            completed_at    TEXT);

        CREATE TABLE outbox(
            seq             INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id        TEXT NOT NULL UNIQUE,
            kind            TEXT NOT NULL,
            route           TEXT NOT NULL,
            payload         TEXT,
            file_path       TEXT,
            content_type    TEXT,
            job_id          TEXT,
            is_terminal     INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT NOT NULL,
            attempts        INTEGER NOT NULL DEFAULT 0,
            next_attempt_at TEXT NOT NULL,
            last_error      TEXT,
            dead            INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_outbox_ready ON outbox(dead, next_attempt_at, seq);

        CREATE TABLE backup_policy(
            policy_id         TEXT PRIMARY KEY,
            policy_version_id TEXT NOT NULL,
            effective_hash    TEXT NOT NULL,
            document          TEXT NOT NULL,
            applied_at        TEXT NOT NULL);

        CREATE TABLE backup_jobs(
            job_id            TEXT PRIMARY KEY,
            kind              TEXT NOT NULL,
            policy_id         TEXT,
            policy_version_id TEXT,
            idempotency_key   TEXT NOT NULL UNIQUE,
            correlation_id    TEXT NOT NULL,
            state             TEXT NOT NULL,
            not_before        TEXT NOT NULL,
            attempt           INTEGER NOT NULL DEFAULT 0,
            max_attempts      INTEGER NOT NULL DEFAULT 3,
            lease_id          TEXT,
            fencing_token     INTEGER,
            lease_expires_at  TEXT,
            repository_id     TEXT,
            engine            TEXT,
            spec_json         TEXT,
            reason_code       TEXT,
            message           TEXT,
            result_json       TEXT,
            scheduled_slot    TEXT,
            created_at        TEXT NOT NULL,
            started_at        TEXT,
            ended_at          TEXT,
            last_sequence     INTEGER NOT NULL DEFAULT 0,
            cancel_requested  INTEGER NOT NULL DEFAULT 0,
            estimated_bytes   INTEGER);
        CREATE INDEX ix_jobs_runnable ON backup_jobs(state, not_before);

        CREATE TABLE backup_checkpoints(
            job_id                TEXT PRIMARY KEY REFERENCES backup_jobs(job_id) ON DELETE CASCADE,
            stage                 TEXT NOT NULL,
            engine_checkpoint_ref TEXT,
            source_snapshot_ref   TEXT,
            last_progress         TEXT,
            updated_at            TEXT NOT NULL);

        CREATE TABLE schedule_state(
            policy_id        TEXT PRIMARY KEY,
            last_slot        TEXT,
            last_success_at  TEXT,
            last_verified_at TEXT,
            last_source_bytes INTEGER);

        CREATE TABLE kv(
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        """;
}
