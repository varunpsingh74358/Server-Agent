using System.Collections.Concurrent;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Data.Sqlite;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// Turns job state changes into durable §12 job events: every transition and its event are
/// written in ONE SQLite transaction (state + outbox row), each event gets the job's next
/// sequence number, a unique event_id (backend inbox de-duplication), schema_version,
/// correlation_id and the lease fencing token. Progress events are rate-limited per job.
/// Messages are passed through <see cref="SecretRedactor"/> - no secret ever reaches an event.
/// </summary>
public sealed class JobEventRecorder(
    BackupJobStore jobs,
    string endpointId,
    TimeProvider time,
    TimeSpan progressInterval)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProgress = new();

    public event Action? EventQueued;

    public BackupJobRecord Transition(
        string jobId,
        JobTransition transition,
        string eventType,
        JobResultSummary? result = null,
        JobProgress? progress = null,
        ResourceSample? resource = null)
    {
        var now = time.GetUtcNow();
        var safe = new JobTransition
        {
            State = transition.State,
            ReasonCode = transition.ReasonCode,
            Message = SecretRedactor.Redact(transition.Message),
            NotBefore = transition.NotBefore,
            IncrementAttempt = transition.IncrementAttempt,
            MarkStarted = transition.MarkStarted,
            MarkEnded = transition.MarkEnded,
            ResultJson = result is null ? transition.ResultJson : JsonSerializer.Serialize(result, BackendApiClient.SnakeCaseJson),
            ClearLease = transition.ClearLease
        };

        var record = jobs.Transition(jobId, safe, now, (connection, transaction, job, sequence) =>
            WriteEvent(connection, transaction, job, sequence, eventType, now, safe.State, safe.ReasonCode, safe.Message, progress, resource, result,
                terminal: BackupJobStates.IsTerminal(safe.State) || safe.State is BackupJobStates.Retrying));
        EventQueued?.Invoke();
        return record;
    }

    /// <summary>Records a progress event unless one was sent for this job within the interval.</summary>
    public bool Progress(string jobId, string stage, JobProgress? progress, ResourceSample? resource, bool force = false)
    {
        var now = time.GetUtcNow();
        if (!force && _lastProgress.TryGetValue(jobId, out var last) && now - last < progressInterval)
        {
            return false;
        }

        _lastProgress[jobId] = now;
        jobs.AppendEvent(jobId, (connection, transaction, job, sequence) =>
            WriteEvent(connection, transaction, job, sequence,
                job.Kind == BackupJobKinds.Restore ? BackupEventTypes.RestoreProgress : BackupEventTypes.Progress,
                now, stage, null, null, progress, resource, null, terminal: false));
        EventQueued?.Invoke();
        return true;
    }

    public void Forget(string jobId) => _lastProgress.TryRemove(jobId, out _);

    private void WriteEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupJobRecord job,
        long sequence,
        string eventType,
        DateTimeOffset now,
        string stage,
        string? reasonCode,
        string? message,
        JobProgress? progress,
        ResourceSample? resource,
        JobResultSummary? result,
        bool terminal)
    {
        var policyHash = job.SpecJson is not null && TryReadPolicyHash(job.SpecJson, out var hash) ? hash : null;
        var evt = new BackupJobEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            CorrelationId = job.CorrelationId,
            JobId = job.JobId,
            JobKind = job.Kind,
            EndpointId = endpointId,
            Sequence = sequence,
            Stage = stage,
            OccurredAt = now,
            PolicyVersion = job.PolicyVersionId,
            PolicyEffectiveHash = policyHash,
            RepositoryId = job.RepositoryId,
            LeaseId = job.LeaseId,
            FencingToken = job.FencingToken,
            Attempt = job.Attempt,
            ReasonCode = reasonCode,
            Message = message,
            Progress = progress,
            Resource = resource,
            Result = result
        };

        var isRestore = job.Kind == BackupJobKinds.Restore;
        OutboxStore.Insert(connection, transaction, new OutboxItem
        {
            EventId = evt.EventId,
            Kind = terminal ? OutboxKinds.JobEvent : OutboxKinds.JobProgress,
            Route = isRestore ? ControlPlaneRoutes.RestoreEvents(job.JobId) : ControlPlaneRoutes.JobEvents(job.JobId),
            Payload = JsonSerializer.Serialize(evt, BackendApiClient.SnakeCaseJson),
            JobId = job.JobId,
            IsTerminal = terminal || eventType is BackupEventTypes.Started or BackupEventTypes.StageChanged
        }, now);
    }

    private static bool TryReadPolicyHash(string specJson, out string? hash)
    {
        hash = null;
        try
        {
            using var doc = JsonDocument.Parse(specJson);
            if (doc.RootElement.TryGetProperty("policyEffectiveHash", out var element) && element.ValueKind == JsonValueKind.String)
            {
                hash = element.GetString();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
