using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker.Engines;

/// <summary>
/// Kopia adapter (ADR-01 preferred prototype). Flags follow the Kopia command-line reference
/// (snapshot create / restore / verify, repository create|connect rclone|s3, policy set,
/// maintenance run). Design points:
/// - per-job config file in the job work dir + --no-persist-credentials: the password is never
///   written anywhere; it is supplied only via KOPIA_PASSWORD.
/// - stable --override-hostname/--override-username from the grant so snapshot history survives
///   reinstalls and machine renames.
/// - retention + VSS + ignore rules are Kopia policy on the source, applied at snapshot time.
/// - file/dir read errors are recorded (not fatal) and surface as PARTIAL.
/// PoC items (CO-DEV-BKP-001 M0): confirm the rclone child inherits Kopia's environment, and the
/// progress line format parsed by <see cref="ParseProgress"/>, against the pinned Kopia version.
/// </summary>
public sealed partial class KopiaEngine : IBackupEngine
{
    public string Name => BackupEngineNames.Kopia;

    private string? _connectedGrantId;

    /// <summary>
    /// Kopia saves the storage credentials it connected with in its (per-job, ACL-protected) config
    /// file and reuses them for every later command. When Core has renewed the grant since, connect
    /// again so the next command runs with the fresh, short-lived credentials (§10).
    /// </summary>
    private async Task<EngineOutcome> EnsureCurrentGrantAsync(EngineContext ctx, CancellationToken ct) =>
        _connectedGrantId is not null && _connectedGrantId != ctx.CurrentGrantId()
            ? await ConnectAsync(ctx, ct).ConfigureAwait(false)
            : EngineOutcome.Success;

    public async Task<EngineOutcome> ConnectAsync(EngineContext ctx, CancellationToken ct)
    {
        var grantId = ctx.CurrentGrantId();
        Directory.CreateDirectory(ctx.CacheDirectory);
        var storage = ctx.Storage();
        var common = new List<string>(storage.KopiaStorageArgs)
        {
            $"--cache-directory={ctx.CacheDirectory}",
            $"--content-cache-size-limit-mb={ctx.Budget.ContentCacheMb}",
            $"--metadata-cache-size-limit-mb={ctx.Budget.MetadataCacheMb}",
            $"--override-hostname={ctx.SnapshotHost}",
            $"--override-username={ctx.SnapshotUser}",
            "--no-check-for-updates"
        };

        var connect = await RunAsync(ctx, ["repository", "connect", .. common], ct).ConfigureAwait(false);
        if (connect.ExitCode != 0)
        {
            var reason = EngineErrorClassifier.Classify(connect.StderrTail);
            if (reason != BackupReasonCodes.RepositoryNotInitialized || !ctx.CreateIfMissing)
            {
                return EngineErrorClassifier.FromRun("kopia", "connect", connect);
            }

            ctx.Log("info", "Repository not initialized yet - creating it (first backup for this repository).");
            var create = await RunAsync(ctx, ["repository", "create", .. common], ct).ConfigureAwait(false);
            if (create.ExitCode != 0)
            {
                return EngineErrorClassifier.FromRun("kopia", "create", create);
            }
        }

        _connectedGrantId = grantId;
        if (ctx.Budget.MaxUploadBytesPerSecond is > 0)
        {
            var throttle = await RunAsync(ctx,
                ["repository", "throttle", "set", $"--upload-bytes-per-second={ctx.Budget.MaxUploadBytesPerSecond.Value.ToString(CultureInfo.InvariantCulture)}"], ct).ConfigureAwait(false);
            if (throttle.ExitCode != 0)
            {
                ctx.Log("warning", "Could not set the Kopia upload throttle; CPU and disk caps still apply.");
            }
        }

        return EngineOutcome.Success;
    }

    public async Task<EngineOutcome> PrepareSourceAsync(EngineContext ctx, BackupSource source, BackupRetention retention, BackupRunOptions options, CancellationToken ct)
    {
        if (await EnsureCurrentGrantAsync(ctx, ct).ConfigureAwait(false) is { Ok: false } reconnect)
        {
            return reconnect;
        }

        var args = new List<string> { "policy", "set", source.Path };
        void Keep(string flag, int? value)
        {
            if (value is not null)
            {
                args.Add($"--{flag}={value.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        Keep("keep-latest", retention.KeepLatest);
        Keep("keep-hourly", retention.KeepHourly);
        Keep("keep-daily", retention.KeepDaily);
        Keep("keep-weekly", retention.KeepWeekly);
        Keep("keep-monthly", retention.KeepMonthly);
        Keep("keep-annual", retention.KeepAnnual);
        args.Add($"--enable-volume-shadow-copy={(options.UseVss ? (options.VssMode == VssModes.Always ? "always" : "when-available") : "never")}");
        args.Add($"--max-parallel-file-reads={Math.Max(1, ctx.Budget.Parallelism).ToString(CultureInfo.InvariantCulture)}");
        args.Add("--ignore-file-errors=true");
        args.Add("--ignore-dir-errors=true");
        args.Add("--one-file-system=true");
        args.Add("--clear-ignore");

        var set = await RunAsync(ctx, args, ct).ConfigureAwait(false);
        if (set.ExitCode != 0)
        {
            return EngineErrorClassifier.FromRun("kopia", "policy", set);
        }

        if (options.Excludes.Count > 0)
        {
            var ignore = new List<string> { "policy", "set", source.Path };
            ignore.AddRange(options.Excludes.Select(e => $"--add-ignore={e}"));
            var add = await RunAsync(ctx, ignore, ct).ConfigureAwait(false);
            if (add.ExitCode != 0)
            {
                return EngineErrorClassifier.FromRun("kopia", "policy", add);
            }
        }

        return EngineOutcome.Success;
    }

    public async Task<SnapshotOutcome> BackupAsync(EngineContext ctx, BackupSource source, BackupRunOptions options, Action<JobProgress, double?> onProgress, CancellationToken ct)
    {
        var lastBytes = 0L;
        var lastAt = DateTimeOffset.UtcNow;
        long? uploaded = null;
        ProgressCounts? counts = null;

        var run = await RunAsync(ctx,
            ["snapshot", "create", source.Path, "--json", $"--parallel={Math.Max(1, ctx.Budget.Parallelism).ToString(CultureInfo.InvariantCulture)}",
             $"--description=CloudOrc job {ctx.JobId}", $"--tags=cloudorc-job:{ctx.JobId}", "--progress", "--progress-update-interval=5s"],
            ct,
            onStderr: line =>
            {
                if (ParseProgress(line) is not { } p)
                {
                    return;
                }

                counts = ParseCounts(line) ?? counts;
                uploaded = p.BytesUploaded ?? uploaded;
                var now = DateTimeOffset.UtcNow;
                double? rate = null;
                if (p.BytesUploaded is { } up && now > lastAt)
                {
                    rate = Math.Max(0, (up - lastBytes) / (now - lastAt).TotalSeconds);
                    lastBytes = up;
                    lastAt = now;
                }

                onProgress(p, rate);
            },
            timeout: options.Timeout).ConfigureAwait(false);

        if (run.ExitCode != 0 || run.TimedOut || run.Cancelled)
        {
            var failed = EngineErrorClassifier.FromRun("kopia", "snapshot", run);
            return new SnapshotOutcome(false, failed.ReasonCode, failed.Message, TimedOut: run.TimedOut);
        }

        return ParseSnapshotManifest(run.StdoutText, uploaded, counts)
               ?? new SnapshotOutcome(false, BackupReasonCodes.EngineFailed, "kopia snapshot finished but its result could not be read.");
    }

    public Task<EngineOutcome> ApplyRetentionAsync(EngineContext ctx, BackupSource source, BackupRetention retention, CancellationToken ct) =>
        Task.FromResult(EngineOutcome.Success); // applied by Kopia itself from the source policy at snapshot time

    public async Task<EngineOutcome> VerifyAsync(EngineContext ctx, IReadOnlyList<string> snapshotIds, double verifyFilesPercent, CancellationToken ct)
    {
        if (await EnsureCurrentGrantAsync(ctx, ct).ConfigureAwait(false) is { Ok: false } reconnect)
        {
            return reconnect;
        }

        var args = new List<string> { "snapshot", "verify" };
        args.AddRange(snapshotIds);
        args.Add($"--verify-files-percent={verifyFilesPercent.ToString("0.###", CultureInfo.InvariantCulture)}");
        args.Add($"--file-parallelism={Math.Max(1, ctx.Budget.Parallelism).ToString(CultureInfo.InvariantCulture)}");
        var run = await RunAsync(ctx, args, ct).ConfigureAwait(false);
        return run.ExitCode == 0 ? EngineOutcome.Success : WithCode(EngineErrorClassifier.FromRun("kopia", "verify", run), BackupReasonCodes.VerificationFailed);
    }

    public async Task<RestoreOutcome> RestoreAsync(EngineContext ctx, string snapshotId, string sourceRoot, string? relativePath, string targetPath, string mode, CancellationToken ct)
    {
        if (await EnsureCurrentGrantAsync(ctx, ct).ConfigureAwait(false) is { Ok: false } reconnect)
        {
            return new RestoreOutcome(false, reconnect.ReasonCode, reconnect.Message);
        }

        var root = await ResolveRootObjectAsync(ctx, snapshotId, ct).ConfigureAwait(false);
        if (root is null)
        {
            return new RestoreOutcome(false, BackupReasonCodes.RestoreTargetInvalid, "The recovery point was not found in the repository.");
        }

        var source = string.IsNullOrEmpty(relativePath) ? root : $"{root}/{relativePath.Replace('\\', '/').TrimStart('/')}";
        var destination = string.IsNullOrEmpty(relativePath) ? targetPath : Path.Combine(targetPath, relativePath);
        var args = new List<string> { "snapshot", "restore", source, destination,$"--parallel={Math.Max(1, ctx.Budget.Parallelism).ToString(CultureInfo.InvariantCulture)}", "--skip-owners", "--no-overwrite-symlinks" };
        switch (mode)
        {
            case RestoreModes.Overwrite:
                args.AddRange(["--overwrite-files", "--overwrite-directories", "--write-files-atomically"]);
                break;
            case RestoreModes.SkipExisting:
                args.AddRange(["--skip-existing", "--no-overwrite-files"]);
                break;
            default: // rename: target is a fresh directory, never existing data
                args.Add("--no-overwrite-files");
                break;
        }

        var run = await RunAsync(ctx, args, ct).ConfigureAwait(false);
        if (run.ExitCode == 0)
        {
            return new RestoreOutcome(true, null, null);
        }

        var failed = EngineErrorClassifier.FromRun("kopia", "restore", run);
        return new RestoreOutcome(false, failed.ReasonCode, failed.Message);
    }

    public async Task<EngineOutcome> MaintenanceAsync(EngineContext ctx, bool full, BackupRetention? retention, CancellationToken ct)
    {
        if (await EnsureCurrentGrantAsync(ctx, ct).ConfigureAwait(false) is { Ok: false } reconnect)
        {
            return reconnect;
        }

        // Maintenance must be owned by the identity running it; the job host/user is stable per repository.
        await RunAsync(ctx, ["maintenance", "set", "--owner=me"], ct).ConfigureAwait(false);
        var run = await RunAsync(ctx, ["maintenance", "run", full ? "--full" : "--no-full"], ct).ConfigureAwait(false);
        return run.ExitCode == 0 ? EngineOutcome.Success : EngineErrorClassifier.FromRun("kopia", "maintenance", run);
    }

    public async Task DisconnectAsync(EngineContext ctx)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await RunAsync(ctx, ["repository", "disconnect"], cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    // ------------------------------------------------------------------ helpers

    private static Task<EngineRunResult> RunAsync(EngineContext ctx, IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStderr = null, TimeSpan? timeout = null)
    {
        var storage = ctx.Storage();
        var env = new Dictionary<string, string?>(storage.Environment, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in EngineErrorClassifier.GoRuntimeEnvironment(ctx.Budget))
        {
            env[k] = v;
        }

        env["KOPIA_PASSWORD"] = ctx.Password;
        env["KOPIA_CHECK_FOR_UPDATES"] = "false";
        env["KOPIA_LOG_DIR"] = Path.Combine(ctx.WorkDirectory, "logs");

        var full = new List<string>(args.Count + 3) { $"--config-file={Path.Combine(ctx.WorkDirectory, "kopia.repository.config")}", "--no-persist-credentials" };
        full.InsertRange(0, args);
        return EngineProcess.RunAsync(ctx.Engine.Path, full, env, ctx.WorkDirectory, null, onStderr, timeout, ct);
    }

    private async Task<string?> ResolveRootObjectAsync(EngineContext ctx, string snapshotId, CancellationToken ct)
    {
        var list = await RunAsync(ctx, ["snapshot", "list", "--json", "--all"], ct).ConfigureAwait(false);
        if (list.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(list.StdoutText, '[', ']') ?? "[]");
            foreach (var manifest in doc.RootElement.EnumerateArray())
            {
                if (manifest.TryGetProperty("id", out var id) && id.GetString() == snapshotId
                    && manifest.TryGetProperty("rootEntry", out var rootEntry) && rootEntry.TryGetProperty("obj", out var obj))
                {
                    return obj.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Counters of the LAST Kopia progress line: hashed = new/changed files, cached = unchanged.</summary>
    internal sealed record ProgressCounts(long Hashed, long HashedBytes, long Cached, long Uploaded);

    internal static ProgressCounts? ParseCounts(string line)
    {
        var hashed = HashedPattern().Match(line);
        var cached = CachedPattern().Match(line);
        if (!hashed.Success && !cached.Success)
        {
            return null;
        }

        static long N(Match m) => m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var uploaded = UploadedPattern().Match(line);
        return new ProgressCounts(
            N(hashed),
            hashed.Success ? EngineErrorClassifier.ParseSize(hashed.Groups[2].Value) ?? 0 : 0,
            N(cached),
            uploaded.Success ? EngineErrorClassifier.ParseSize(uploaded.Groups[1].Value) ?? 0 : 0);
    }

    /// <summary>
    /// Kopia 0.23's `snapshot create --json` prints the snapshot manifest only (rootEntry.summ: size,
    /// files; no "stats" block), so new/changed vs unchanged come from the last progress line
    /// (hashed vs cached). Its "uploaded" counter is printed before the final pack flush, so the bytes
    /// of new data are reported as the larger of uploaded and hashed bytes (an upper bound after dedup).
    /// </summary>
    internal static SnapshotOutcome? ParseSnapshotManifest(string stdout, long? bytesUploaded, ProgressCounts? counts = null)
    {
        var json = ExtractJson(stdout, '{', '}');
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            string? rootObj = null;
            long? files = null, bytes = null, errors = null;
            if (m.TryGetProperty("rootEntry", out var root))
            {
                rootObj = root.TryGetProperty("obj", out var o) ? o.GetString() : null;
                if (root.TryGetProperty("summ", out var summ))
                {
                    files = Long(summ, "files");
                    bytes = Long(summ, "size");
                    errors = Long(summ, "numFailed") ?? Long(summ, "errors");
                }
            }

            long? cached = null, nonCached = null, errorCount = null;
            if (m.TryGetProperty("stats", out var stats))
            {
                bytes ??= Long(stats, "totalSize");
                files ??= Long(stats, "fileCount");
                cached = Long(stats, "cachedFiles");
                nonCached = Long(stats, "nonCachedFiles");
                errorCount = Long(stats, "errorCount");
            }

            if (counts is not null)
            {
                nonCached ??= counts.Hashed;
                cached ??= counts.Cached;
                bytesUploaded = Math.Max(bytesUploaded ?? 0, Math.Max(counts.Uploaded, counts.HashedBytes));
            }

            return new SnapshotOutcome(true, null, null, id, rootObj, files, bytes, errors ?? errorCount ?? 0,
                FilesNew: null, FilesChanged: nonCached, FilesUnchanged: cached, BytesUploaded: bytesUploaded);
        }
        catch (JsonException)
        {
            return null;
        }

        static long? Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    }

    private static string? ExtractJson(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        var end = text.LastIndexOf(close);
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>
    /// Lenient parse of Kopia's periodic progress line, e.g.
    /// " * 0 hashing, 1250 hashed (1.2 GB), 300 cached (5 GB), uploaded 400 MB, estimated 10 GB (50.0%) 2m left".
    /// Unrecognized lines return null (progress is best-effort; it never affects the job result).
    /// </summary>
    internal static JobProgress? ParseProgress(string line)
    {
        var hashed = HashedPattern().Match(line);
        var cached = CachedPattern().Match(line);
        var uploaded = UploadedPattern().Match(line);
        var estimated = EstimatedPattern().Match(line);
        if (!hashed.Success && !uploaded.Success && !estimated.Success)
        {
            return null;
        }

        long? Count(Match m) => m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        var hashedBytes = hashed.Success ? EngineErrorClassifier.ParseSize(hashed.Groups[2].Value) : null;
        var cachedBytes = cached.Success ? EngineErrorClassifier.ParseSize(cached.Groups[2].Value) : null;
        double? percent = estimated.Success && double.TryParse(estimated.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? pct : null;

        return new JobProgress
        {
            FilesDone = (Count(hashed) ?? 0) + (Count(cached) ?? 0),
            BytesRead = (hashedBytes ?? 0) + (cachedBytes ?? 0),
            BytesUploaded = uploaded.Success ? EngineErrorClassifier.ParseSize(uploaded.Groups[1].Value) : null,
            BytesTotal = estimated.Success ? EngineErrorClassifier.ParseSize(estimated.Groups[1].Value) : null,
            Percent = percent
        };
    }

    private static EngineOutcome WithCode(EngineOutcome outcome, string code) =>
        outcome.ReasonCode is BackupReasonCodes.Cancelled or BackupReasonCodes.StorageUnavailable or BackupReasonCodes.StorageThrottled or BackupReasonCodes.StorageCredentialExpired
            ? outcome
            : outcome with { ReasonCode = code };

    [GeneratedRegex(@"(\d+) hashed \(([\d.,]+ ?[KMGTP]?i?B)\)", RegexOptions.IgnoreCase)]
    private static partial Regex HashedPattern();

    [GeneratedRegex(@"(\d+) cached \(([\d.,]+ ?[KMGTP]?i?B)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CachedPattern();

    [GeneratedRegex(@"uploaded ([\d.,]+ ?[KMGTP]?i?B)", RegexOptions.IgnoreCase)]
    private static partial Regex UploadedPattern();

    [GeneratedRegex(@"estimated ([\d.,]+ ?[KMGTP]?i?B) \(([\d.]+)%\)", RegexOptions.IgnoreCase)]
    private static partial Regex EstimatedPattern();
}
