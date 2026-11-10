using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker;

/// <summary>Messages the job logic sends to Agent Core.</summary>
public interface IWorkerReporter
{
    void Stage(string stage);

    void Progress(JobProgress progress, double? uploadBytesPerSecond = null, long? scannerDurationMs = null);

    void Log(string level, string message);

    /// <summary>Durable per-source checkpoint (Core persists it, so a retry skips this source).</summary>
    void SnapshotCompleted(SnapshotRef snapshot);

    /// <summary>The current (possibly renewed) storage grant. SECRET - never log it.</summary>
    StorageGrant CurrentGrant { get; }

    /// <summary>Asks Core for a fresh grant; returns the new one, or null if renewal failed.</summary>
    Task<StorageGrant?> RenewGrantAsync(CancellationToken cancellationToken);
}

/// <summary>
/// JSON-lines link with Agent Core over this process's stdin/stdout. stdout carries ONLY protocol
/// messages; anything else the worker wants to say goes through <see cref="Log"/>. Every text
/// leaving the worker passes the shared <see cref="SecretRedactor"/>.
/// </summary>
public sealed class WorkerChannel : IWorkerReporter
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Lock _writeLock = new();
    private readonly Lock _grantLock = new();
    private TaskCompletionSource<StorageGrant?>? _pendingRenewal;
    private StorageGrant _grant;

    public WorkerChannel(TextReader input, TextWriter output, StorageGrant initialGrant)
    {
        _input = input;
        _output = output;
        _grant = initialGrant;
    }

    public CancellationTokenSource Cancellation { get; } = new();

    public StorageGrant CurrentGrant
    {
        get
        {
            lock (_grantLock)
            {
                return _grant;
            }
        }
    }

    /// <summary>Reads the first line, which must be the "start" envelope.</summary>
    public static async Task<WorkerJobSpec?> ReadStartAsync(TextReader input)
    {
        var line = await input.ReadLineAsync().ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }

        // Tolerate a UTF-8 byte-order mark from callers whose stdin encoder writes one.
        var envelope = JsonSerializer.Deserialize<WorkerEnvelope>(line.TrimStart('﻿'), WorkerEnvelope.JsonOptions);
        return envelope?.Type == WorkerMessageTypes.Start ? envelope.Spec : null;
    }

    /// <summary>Background loop handling cancel / grant messages from Core. Core closing stdin = cancel.</summary>
    public async Task ListenAsync()
    {
        try
        {
            string? line;
            while ((line = await _input.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                WorkerEnvelope? message;
                try
                {
                    message = JsonSerializer.Deserialize<WorkerEnvelope>(line, WorkerEnvelope.JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                switch (message?.Type)
                {
                    case WorkerMessageTypes.Cancel:
                        await Cancellation.CancelAsync().ConfigureAwait(false);
                        break;
                    case WorkerMessageTypes.GrantRenewed when message.Grant is not null:
                        lock (_grantLock)
                        {
                            _grant = message.Grant;
                        }

                        _pendingRenewal?.TrySetResult(message.Grant);
                        break;
                    case WorkerMessageTypes.GrantRenewFailed:
                        _pendingRenewal?.TrySetResult(null);
                        break;
                }
            }
        }
        catch (IOException)
        {
        }

        // Supervisor went away: never keep running unsupervised.
        await Cancellation.CancelAsync().ConfigureAwait(false);
    }

    public void Stage(string stage) => Send(new WorkerEnvelope { Type = WorkerMessageTypes.Stage, Stage = stage });

    public void Progress(JobProgress progress, double? uploadBytesPerSecond = null, long? scannerDurationMs = null) =>
        Send(new WorkerEnvelope { Type = WorkerMessageTypes.Progress, Progress = progress, UploadBytesPerSecond = uploadBytesPerSecond, ScannerDurationMs = scannerDurationMs });

    public void SnapshotCompleted(SnapshotRef snapshot) =>
        Send(new WorkerEnvelope { Type = WorkerMessageTypes.Checkpoint, Snapshot = snapshot });

    public void Log(string level, string message) =>
        Send(new WorkerEnvelope { Type = WorkerMessageTypes.Log, Level = level, Message = SecretRedactor.Redact(message) });

    public void Result(WorkerResult result) =>
        Send(new WorkerEnvelope
        {
            Type = WorkerMessageTypes.Result,
            Result = new WorkerResult
            {
                State = result.State,
                ReasonCode = result.ReasonCode,
                Message = SecretRedactor.Redact(result.Message),
                Summary = result.Summary
            }
        });

    public async Task<StorageGrant?> RenewGrantAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<StorageGrant?> pending;
        lock (_grantLock)
        {
            pending = _pendingRenewal is { Task.IsCompleted: false } existing
                ? existing
                : _pendingRenewal = new TaskCompletionSource<StorageGrant?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Send(new WorkerEnvelope { Type = WorkerMessageTypes.GrantRenewRequest });
        var finished = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(90), cancellationToken)).ConfigureAwait(false);
        return finished == pending.Task ? await pending.Task.ConfigureAwait(false) : null;
    }

    private void Send(WorkerEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, WorkerEnvelope.JsonOptions);
        lock (_writeLock)
        {
            _output.WriteLine(json);
            _output.Flush();
        }
    }
}
