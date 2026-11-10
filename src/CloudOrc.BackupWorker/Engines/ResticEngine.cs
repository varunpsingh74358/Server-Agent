using System.Globalization;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker.Engines;

/// <summary>
/// Restic adapter (ADR-01 "Restic remains the mandatory benchmark challenger"). Uses restic's
/// machine-readable --json output (status / summary / error messages), RESTIC_PASSWORD from the
/// environment only, a bounded RESTIC_CACHE_DIR, and Windows --use-fs-snapshot for VSS.
/// Exit code 3 ("snapshot created, some source files could not be read") becomes PARTIAL.
/// Retention = forget after each backup (cheap); data deletion = prune in maintenance (§7
/// "Maintenance throttled and isolated").
/// </summary>
public sealed class ResticEngine : IBackupEngine
{
    private const int ExitIncomplete = 3;
    private const int ExitRepositoryMissing = 10;

    public string Name => BackupEngineNames.Restic;

    public async Task<EngineOutcome> ConnectAsync(EngineContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(ctx.CacheDirectory);
        var probe = await RunAsync(ctx, ["cat", "config"], ct).ConfigureAwait(false);
        if (probe.ExitCode == 0)
        {
            return EngineOutcome.Success;
        }

        var missing = probe.ExitCode == ExitRepositoryMissing
                      || EngineErrorClassifier.Classify(probe.StderrTail) == BackupReasonCodes.RepositoryNotInitialized;
        if (!missing || !ctx.CreateIfMissing)
        {
            return EngineErrorClassifier.FromRun("restic", "open repository", probe);
        }

        ctx.Log("info", "Repository not initialized yet - creating it (first backup for this repository).");
        var init = await RunAsync(ctx, ["init"], ct).ConfigureAwait(false);
        return init.ExitCode == 0 ? EngineOutcome.Success : EngineErrorClassifier.FromRun("restic", "init", init);
    }

    public Task<EngineOutcome> PrepareSourceAsync(EngineContext ctx, BackupSource source, BackupRetention retention, BackupRunOptions options, CancellationToken ct) =>
        Task.FromResult(EngineOutcome.Success);

    public async Task<SnapshotOutcome> BackupAsync(EngineContext ctx, BackupSource source, BackupRunOptions options, Action<JobProgress, double?> onProgress, CancellationToken ct)
    {
        var args = new List<string>
        {
            "backup", source.Path, "--json",
            $"--host={ctx.SnapshotHost}",
            "--tag=cloudorc", $"--tag=cloudorc-job:{ctx.JobId}",
            $"--read-concurrency={Math.Max(1, ctx.Budget.Parallelism).ToString(CultureInfo.InvariantCulture)}",
            "--one-file-system"
        };

        if (options.UseVss)
        {
            args.Add("--use-fs-snapshot");
        }

        if (ctx.Budget.MaxUploadBytesPerSecond is > 0)
        {
            args.Add($"--limit-upload={Math.Max(1, ctx.Budget.MaxUploadBytesPerSecond.Value / 1024).ToString(CultureInfo.InvariantCulture)}");
        }

        args.AddRange(options.Excludes.Select(e => $"--exclude={e}"));

        string? snapshotId = null;
        long? filesNew = null, filesChanged = null, filesUnmodified = null, added = null, totalFiles = null, totalBytes = null;
        long errors = 0;
        var lastBytes = 0L;
        var lastAt = DateTimeOffset.UtcNow;

        var run = await RunAsync(ctx, args, ct, onStdout: line =>
        {
            if (!line.StartsWith('{'))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var m = doc.RootElement;
                switch (m.TryGetProperty("message_type", out var t) ? t.GetString() : null)
                {
                    case "status":
                        var done = Long(m, "bytes_done");
                        var now = DateTimeOffset.UtcNow;
                        double? rate = null;
                        if (done is { } d && now > lastAt)
                        {
                            rate = Math.Max(0, (d - lastBytes) / (now - lastAt).TotalSeconds);
                            lastBytes = d;
                            lastAt = now;
                        }

                        onProgress(new JobProgress
                        {
                            FilesTotal = Long(m, "total_files"),
                            FilesDone = Long(m, "files_done"),
                            BytesTotal = Long(m, "total_bytes"),
                            BytesRead = done,
                            Percent = m.TryGetProperty("percent_done", out var p) && p.ValueKind == JsonValueKind.Number ? Math.Round(p.GetDouble() * 100, 1) : null,
                            EtaSeconds = Long(m, "seconds_remaining"),
                            Errors = Long(m, "error_count")
                        }, rate);
                        break;
                    case "summary":
                        snapshotId = m.TryGetProperty("snapshot_id", out var s) ? s.GetString() : null;
                        filesNew = Long(m, "files_new");
                        filesChanged = Long(m, "files_changed");
                        filesUnmodified = Long(m, "files_unmodified");
                        added = Long(m, "data_added");
                        totalFiles = Long(m, "total_files_processed");
                        totalBytes = Long(m, "total_bytes_processed");
                        break;
                    case "error":
                        errors++;
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }, timeout: options.Timeout).ConfigureAwait(false);

        if ((run.ExitCode == 0 || run.ExitCode == ExitIncomplete) && !run.TimedOut && !run.Cancelled && snapshotId is not null)
        {
            if (run.ExitCode == ExitIncomplete && errors == 0)
            {
                errors = 1;
            }

            return new SnapshotOutcome(true, null, null, snapshotId, null, totalFiles, totalBytes, errors,
                filesNew, filesChanged, filesUnmodified, added);
        }

        var failed = EngineErrorClassifier.FromRun("restic", "backup", run);
        return new SnapshotOutcome(false, failed.ReasonCode, failed.Message, TimedOut: run.TimedOut);
    }

    public async Task<EngineOutcome> ApplyRetentionAsync(EngineContext ctx, BackupSource source, BackupRetention retention, CancellationToken ct)
    {
        var args = new List<string> { "forget", $"--host={ctx.SnapshotHost}", $"--path={source.Path}", "--tag=cloudorc", "--group-by=host,paths" };
        AddKeep(args, retention);
        var run = await RunAsync(ctx, args, ct).ConfigureAwait(false);
        return run.ExitCode == 0 ? EngineOutcome.Success : EngineErrorClassifier.FromRun("restic", "forget", run);
    }

    public async Task<EngineOutcome> VerifyAsync(EngineContext ctx, IReadOnlyList<string> snapshotIds, double verifyFilesPercent, CancellationToken ct)
    {
        var args = new List<string> { "check" };
        if (verifyFilesPercent > 0)
        {
            args.Add($"--read-data-subset={verifyFilesPercent.ToString("0.###", CultureInfo.InvariantCulture)}%");
        }

        var run = await RunAsync(ctx, args, ct).ConfigureAwait(false);
        if (run.ExitCode == 0)
        {
            return EngineOutcome.Success;
        }

        var failed = EngineErrorClassifier.FromRun("restic", "check", run);
        return failed.ReasonCode is BackupReasonCodes.Cancelled or BackupReasonCodes.StorageUnavailable or BackupReasonCodes.StorageThrottled or BackupReasonCodes.StorageCredentialExpired or BackupReasonCodes.RepositoryLocked
            ? failed
            : failed with { ReasonCode = BackupReasonCodes.VerificationFailed };
    }

    public async Task<RestoreOutcome> RestoreAsync(EngineContext ctx, string snapshotId, string sourceRoot, string? relativePath, string targetPath, string mode, CancellationToken ct)
    {
        // restic stores "D:\Tally\Data" as "/D/Tally/Data"; "<id>:<subfolder>" restores that
        // folder's CONTENTS into the target, which is what the portal selection means.
        var args = new List<string> { "restore", $"{snapshotId}:{ToResticPath(sourceRoot)}", $"--target={targetPath}" };
        if (!string.IsNullOrEmpty(relativePath))
        {
            args.Add($"--include=/{relativePath.Replace('\\', '/').TrimStart('/')}");
        }

        args.Add(mode switch
        {
            RestoreModes.Overwrite => "--overwrite=always",
            RestoreModes.SkipExisting => "--overwrite=never",
            _ => "--overwrite=never"
        });

        var run = await RunAsync(ctx, args, ct).ConfigureAwait(false);
        if (run.ExitCode == 0)
        {
            return new RestoreOutcome(true, null, null);
        }

        var failed = EngineErrorClassifier.FromRun("restic", "restore", run);
        return new RestoreOutcome(false, failed.ReasonCode, failed.Message);
    }

    public async Task<EngineOutcome> MaintenanceAsync(EngineContext ctx, bool full, BackupRetention? retention, CancellationToken ct)
    {
        if (retention is not null)
        {
            var forget = new List<string> { "forget", "--tag=cloudorc", "--group-by=host,paths" };
            AddKeep(forget, retention);
            var f = await RunAsync(ctx, forget, ct).ConfigureAwait(false);
            if (f.ExitCode != 0)
            {
                return EngineErrorClassifier.FromRun("restic", "forget", f);
            }
        }

        var run = await RunAsync(ctx, full ? ["prune"] : ["check"], ct).ConfigureAwait(false);
        return run.ExitCode == 0 ? EngineOutcome.Success : EngineErrorClassifier.FromRun("restic", full ? "prune" : "check", run);
    }

    public Task DisconnectAsync(EngineContext ctx) => Task.CompletedTask;

    internal static string ToResticPath(string windowsPath)
    {
        var full = windowsPath.Replace('\\', '/').TrimEnd('/');
        return full.Length >= 2 && full[1] == ':' ? "/" + full[0] + full[2..] : full;
    }

    private static void AddKeep(List<string> args, BackupRetention r)
    {
        void Keep(string flag, int? value)
        {
            if (value is > 0)
            {
                args.Add($"--{flag}={value.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        Keep("keep-last", r.KeepLatest);
        Keep("keep-hourly", r.KeepHourly);
        Keep("keep-daily", r.KeepDaily);
        Keep("keep-weekly", r.KeepWeekly);
        Keep("keep-monthly", r.KeepMonthly);
        Keep("keep-yearly", r.KeepAnnual);
    }

    private static Task<EngineRunResult> RunAsync(EngineContext ctx, IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStdout = null, TimeSpan? timeout = null)
    {
        var storage = ctx.Storage();
        var env = new Dictionary<string, string?>(storage.Environment, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in EngineErrorClassifier.GoRuntimeEnvironment(ctx.Budget))
        {
            env[k] = v;
        }

        env["RESTIC_PASSWORD"] = ctx.Password;
        env["RESTIC_CACHE_DIR"] = ctx.CacheDirectory;
        env["RESTIC_PROGRESS_FPS"] = "0.2";

        var full = new List<string> { "-r", storage.ResticRepository };
        full.AddRange(storage.ResticOptions);
        full.AddRange(args);
        return EngineProcess.RunAsync(ctx.Engine.Path, full, env, ctx.WorkDirectory, onStdout, null, timeout, ct, captureStdout: onStdout is null);
    }

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
