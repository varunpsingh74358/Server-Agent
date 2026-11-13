using System.Globalization;
using System.Text.RegularExpressions;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker.Storage;

namespace CloudOrc.BackupWorker.Engines;

/// <summary>Per-job engine context. <see cref="Storage"/> is re-read on every call so a renewed grant is used for the next engine invocation.</summary>
public sealed class EngineContext
{
    public required EngineBinary Engine { get; init; }
    public required Func<EngineStorage> Storage { get; init; }

    /// <summary>Id of the grant currently in force (changes when Core renews it).</summary>
    public Func<string> CurrentGrantId { get; init; } = () => "";
    public required string WorkDirectory { get; init; }
    public required string CacheDirectory { get; init; }
    public required WorkerBudget Budget { get; init; }

    /// <summary>Stable identity snapshots are recorded under (from the grant), never the volatile machine name.</summary>
    public required string SnapshotHost { get; init; }
    public required string SnapshotUser { get; init; }
    public required string JobId { get; init; }

    /// <summary>Repository password. SECRET - env only.</summary>
    public required string Password { get; init; }
    public required bool CreateIfMissing { get; init; }
    public required Action<string, string> Log { get; init; }
}

public record EngineOutcome(bool Ok, string? ReasonCode, string? Message)
{
    public static readonly EngineOutcome Success = new(true, null, null);

    public static EngineOutcome Fail(string reason, string message) => new(false, reason, message);
}

public sealed record SnapshotOutcome(
    bool Ok,
    string? ReasonCode,
    string? Message,
    string? SnapshotId = null,
    string? RootObjectId = null,
    long? Files = null,
    long? Bytes = null,
    long? Errors = null,
    long? FilesNew = null,
    long? FilesChanged = null,
    long? FilesUnchanged = null,
    long? BytesUploaded = null,
    bool TimedOut = false) : EngineOutcome(Ok, ReasonCode, Message);

public sealed record RestoreOutcome(bool Ok, string? ReasonCode, string? Message) : EngineOutcome(Ok, ReasonCode, Message);

public sealed record BackupRunOptions(bool UseVss, string VssMode, TimeSpan? Timeout, IReadOnlyList<string> Excludes, bool FullScan = false);

/// <summary>
/// Engine adapter (§14 "agent/Capabilities/Backup/EngineAdapters"; ADR-01: Kopia preferred,
/// Restic mandatory benchmark challenger - so both stay behind this one interface and nothing
/// above it knows which engine runs). Repositories are engine-specific (§9 "cannot transparently
/// interchange Kopia and Restic repositories").
/// </summary>
public interface IBackupEngine
{
    string Name { get; }

    Task<EngineOutcome> ConnectAsync(EngineContext context, CancellationToken cancellationToken);

    /// <summary>Kopia applies retention as policy at snapshot time; Restic via forget afterwards.</summary>
    Task<EngineOutcome> PrepareSourceAsync(EngineContext context, BackupSource source, BackupRetention retention, BackupRunOptions options, CancellationToken cancellationToken);

    Task<SnapshotOutcome> BackupAsync(EngineContext context, BackupSource source, BackupRunOptions options, Action<JobProgress, double?> onProgress, CancellationToken cancellationToken);

    Task<EngineOutcome> ApplyRetentionAsync(EngineContext context, BackupSource source, BackupRetention retention, CancellationToken cancellationToken);

    Task<EngineOutcome> VerifyAsync(EngineContext context, IReadOnlyList<string> snapshotIds, double verifyFilesPercent, CancellationToken cancellationToken);

    Task<RestoreOutcome> RestoreAsync(EngineContext context, string snapshotId, string sourceRoot, string? relativePath, string targetPath, string mode, CancellationToken cancellationToken);

    Task<EngineOutcome> MaintenanceAsync(EngineContext context, bool full, BackupRetention? retention, CancellationToken cancellationToken);

    Task DisconnectAsync(EngineContext context);
}

/// <summary>Maps engine/storage error text to stable reason codes (§12 reason_code, §8 distinguish error classes).</summary>
public static class EngineErrorClassifier
{
    private static readonly (Regex Pattern, string Code)[] Rules =
    [
        (new Regex(@"wrong password|invalid repository password|password is incorrect|ciphertext verification failed|no key found", RegexOptions.IgnoreCase), BackupReasonCodes.RepositoryPasswordInvalid),
        (new Regex(@"not initialized|repository does not exist|unable to open config file|Is there a repository at|repository not found|unable to find repository", RegexOptions.IgnoreCase), BackupReasonCodes.RepositoryNotInitialized),
        (new Regex(@"repository is already locked|unable to create lock|lock .* exclusive|lock held", RegexOptions.IgnoreCase), BackupReasonCodes.RepositoryLocked),
        (new Regex(@"storageQuotaExceeded|quota exceeded|insufficient storage|no space left|QuotaExceeded|disk full", RegexOptions.IgnoreCase), BackupReasonCodes.StorageFull),
        (new Regex(@"userRateLimitExceeded|rateLimitExceeded|upload limit|uploadLimit|too many requests|\b429\b|SlowDown|TooManyRequests", RegexOptions.IgnoreCase), BackupReasonCodes.StorageThrottled),
        (new Regex(@"invalid_grant|token expired|ExpiredToken|\b401\b|unauthorized|invalid credentials|authError", RegexOptions.IgnoreCase), BackupReasonCodes.StorageCredentialExpired),
        (new Regex(@"volume shadow|shadow copy|\bVSS\b|VSS_E_", RegexOptions.IgnoreCase), BackupReasonCodes.SnapshotFailed),
        (new Regex(@"corrupt|checksum mismatch|hash mismatch|invalid blob|pack .* not found|ciphertext|unexpected content", RegexOptions.IgnoreCase), BackupReasonCodes.RepositoryCorrupt),
        (new Regex(@"no such host|connection refused|timeout|timed out|\b50[0234]\b|ServiceUnavailable|connection reset|TLS handshake", RegexOptions.IgnoreCase), BackupReasonCodes.StorageUnavailable),
        (new Regex(@"access denied|AccessDenied|\b403\b|forbidden", RegexOptions.IgnoreCase), BackupReasonCodes.StorageGrantFailed)
    ];

    public static string Classify(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        foreach (var (pattern, code) in Rules)
        {
            if (pattern.IsMatch(text))
            {
                return code;
            }
        }

        return BackupReasonCodes.EngineFailed;
    }

    public static EngineOutcome FromRun(string engine, string operation, EngineRunResult run)
    {
        if (run.Cancelled)
        {
            return EngineOutcome.Fail(BackupReasonCodes.Cancelled, "Cancelled.");
        }

        if (run.TimedOut)
        {
            return EngineOutcome.Fail(BackupReasonCodes.SnapshotLifetimeExceeded, $"{engine} {operation} exceeded the maximum snapshot lifetime and was stopped.");
        }

        var code = Classify(run.StderrTail);
        var last = run.StderrTail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? $"exit code {run.ExitCode}";
        return EngineOutcome.Fail(code, $"{engine} {operation} failed: {last}");
    }

    /// <summary>"1.5 GB", "300 MiB", "12 B" → bytes. Null if unparseable.</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text.Trim(), @"^([\d.,]+)\s*([KMGTP]?i?B)$", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var unit = match.Groups[2].Value.ToUpperInvariant();
        var binary = unit.Contains('I', StringComparison.Ordinal);
        var multiplier = unit[0] switch
        {
            'K' => binary ? 1024d : 1e3,
            'M' => binary ? Math.Pow(1024, 2) : 1e6,
            'G' => binary ? Math.Pow(1024, 3) : 1e9,
            'T' => binary ? Math.Pow(1024, 4) : 1e12,
            'P' => binary ? Math.Pow(1024, 5) : 1e15,
            _ => 1d
        };
        return (long)(number * multiplier);
    }

    public static Dictionary<string, string?> GoRuntimeEnvironment(WorkerBudget budget) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["GOMAXPROCS"] = Math.Max(1, budget.GoMaxProcs).ToString(CultureInfo.InvariantCulture),
        ["GOMEMLIMIT"] = $"{Math.Max(64, budget.GoMemoryLimitMb)}MiB"
    };
}
