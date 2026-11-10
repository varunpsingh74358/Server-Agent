using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.ResourceGovernor;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>Callbacks from a running worker into the job runner.</summary>
public interface IWorkerCallbacks
{
    void OnStage(string stage);

    void OnSnapshotCompleted(SnapshotRef snapshot);

    void OnProgress(JobProgress? progress, double? uploadBytesPerSecond, long? scannerDurationMs);

    /// <summary>Worker asks for a fresh storage grant (token about to expire). Null = could not renew.</summary>
    Task<StorageGrant?> RenewGrantAsync(CancellationToken cancellationToken);
}

public interface IWorkerHost
{
    Task<WorkerResult> RunAsync(
        WorkerJobSpec spec,
        ResourceBudget budget,
        IReadOnlyCollection<string> volumeRoots,
        bool maintenance,
        IWorkerCallbacks callbacks,
        Func<ResourceSample?, Task>? onResourceSample,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs ONE job in the isolated CloudOrc.BackupWorker.exe process (§3, §13: "keep Agent Core
/// responsive if backup worker crashes"). The worker is placed in the governor's Job Object
/// BEFORE it is told to start, so every engine process it spawns inherits the caps. The spec -
/// which contains the grant - is written to the worker's stdin only; nothing secret appears on
/// the command line or disk. A crash, hang or kill of the worker becomes a FAILED/WORKER_CRASHED
/// result for that job only; Agent Core carries on.
/// </summary>
public sealed class ProcessWorkerHost(
    BackupPaths paths,
    IResourceGovernor governor,
    ILogger<ProcessWorkerHost> logger) : IWorkerHost
{
    private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(60);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public async Task<WorkerResult> RunAsync(
        WorkerJobSpec spec,
        ResourceBudget budget,
        IReadOnlyCollection<string> volumeRoots,
        bool maintenance,
        IWorkerCallbacks callbacks,
        Func<ResourceSample?, Task>? onResourceSample,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.WorkerExecutable))
        {
            return new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.EngineBinaryMissing, Message = "Backup worker is not installed." };
        }

        var startInfo = new ProcessStartInfo(paths.WorkerExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.Paths.WorkDirectory
        };
        startInfo.ArgumentList.Add("--job-id");
        startInfo.ArgumentList.Add(spec.JobId);

        Directory.CreateDirectory(spec.Paths.WorkDirectory);

        await using var session = governor.CreateSession(budget, volumeRoots, maintenance);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.WorkerCrashed, Message = "Backup worker could not be started." };
        }

        try
        {
            session.Start(process);
        }
        catch (Exception ex)
        {
            // Never run a job outside its budget (§7).
            logger.LogError(ex, "Could not place the backup worker under its resource budget; aborting job {JobId}.", spec.JobId);
            TryKill(process);
            return new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.ResourceBudgetExceeded, Message = "Resource governor could not be applied." };
        }

        var stdin = process.StandardInput;
        var writeLock = new SemaphoreSlim(1, 1);
        async Task SendAsync(WorkerEnvelope envelope)
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await stdin.WriteLineAsync(JsonSerializer.Serialize(envelope, WorkerEnvelope.JsonOptions)).ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Worker already gone; its exit is handled below.
            }
            finally
            {
                writeLock.Release();
            }
        }

        WorkerResult? result = null;
        var stderrTask = PumpStderrAsync(process, spec.JobId);
        using var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampleTask = PumpResourceSamplesAsync(session, onResourceSample, samplingCts.Token);

        await SendAsync(new WorkerEnvelope { Type = WorkerMessageTypes.Start, Spec = spec }).ConfigureAwait(false);

        using var cancelRegistration = cancellationToken.Register(() =>
        {
            _ = SendAsync(new WorkerEnvelope { Type = WorkerMessageTypes.Cancel });
            _ = Task.Delay(CancelGracePeriod).ContinueWith(_ => TryKill(process), TaskScheduler.Default);
        });

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                WorkerEnvelope? message;
                try
                {
                    message = JsonSerializer.Deserialize<WorkerEnvelope>(line, WorkerEnvelope.JsonOptions);
                }
                catch (JsonException)
                {
                    logger.LogDebug("[worker {JobId}] {Line}", spec.JobId, SecretRedactor.Redact(line));
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                switch (message.Type)
                {
                    case WorkerMessageTypes.Stage when message.Stage is not null:
                        callbacks.OnStage(message.Stage);
                        break;

                    case WorkerMessageTypes.Progress:
                        session.UploadBytesPerSecond = message.UploadBytesPerSecond ?? session.UploadBytesPerSecond;
                        session.ScannerDurationMs = message.ScannerDurationMs ?? session.ScannerDurationMs;
                        callbacks.OnProgress(message.Progress, message.UploadBytesPerSecond, message.ScannerDurationMs);
                        break;

                    case WorkerMessageTypes.GrantRenewRequest:
                        _ = Task.Run(async () =>
                        {
                            var grant = await callbacks.RenewGrantAsync(cancellationToken).ConfigureAwait(false);
                            await SendAsync(grant is null
                                ? new WorkerEnvelope { Type = WorkerMessageTypes.GrantRenewFailed }
                                : new WorkerEnvelope { Type = WorkerMessageTypes.GrantRenewed, Grant = grant }).ConfigureAwait(false);
                        }, CancellationToken.None);
                        break;

                    case WorkerMessageTypes.Checkpoint when message.Snapshot is not null:
                        callbacks.OnSnapshotCompleted(message.Snapshot);
                        break;

                    case WorkerMessageTypes.Log:
                        LogFromWorker(spec.JobId, message.Level, message.Message);
                        break;

                    case WorkerMessageTypes.Result when message.Result is not null:
                        result = message.Result;
                        break;
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await samplingCts.CancelAsync().ConfigureAwait(false);
            TryKill(process);
            await stderrTask.ConfigureAwait(false);
        }

        try
        {
            await sampleTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        session.CacheSizeBytes = BackupPaths.DirectorySize(spec.Paths.CacheDirectory);
        if (result is not null)
        {
            return result;
        }

        return cancellationToken.IsCancellationRequested
            ? new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled, Message = "Job was cancelled." }
            : new WorkerResult
            {
                State = BackupJobStates.Failed,
                ReasonCode = BackupReasonCodes.WorkerCrashed,
                Message = $"Backup worker exited unexpectedly (exit code {SafeExitCode(process)})."
            };
    }

    private async Task PumpResourceSamplesAsync(GovernorSession session, Func<ResourceSample?, Task>? onSample, CancellationToken cancellationToken)
    {
        if (onSample is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            await onSample(session.LatestSample).ConfigureAwait(false);
        }
    }

    private async Task PumpStderrAsync(Process process, string jobId)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                logger.LogDebug("[worker {JobId} stderr] {Line}", jobId, SecretRedactor.Redact(line));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private void LogFromWorker(string jobId, string? level, string? message)
    {
        var text = SecretRedactor.Redact(message) ?? string.Empty;
        switch (level)
        {
            case "error":
                logger.LogError("[worker {JobId}] {Message}", jobId, text);
                break;
            case "warning":
                logger.LogWarning("[worker {JobId}] {Message}", jobId, text);
                break;
            default:
                logger.LogInformation("[worker {JobId}] {Message}", jobId, text);
                break;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or ObjectDisposedException)
        {
        }
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }
}
