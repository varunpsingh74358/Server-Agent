using System.Net;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.ResourceGovernor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Backup;

public sealed class BackupJobRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-runner-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStateDatabase _db;
    private readonly BackupJobStore _jobs;
    private readonly BackupPolicyStore _policies;
    private readonly OutboxStore _outbox;
    private readonly FakeControlPlane _plane = new();
    private readonly FakeWorkerHost _worker = new();
    private readonly BackupJobRunner _runner;

    public BackupJobRunnerTests()
    {
        _db = new AgentStateDatabase(Path.Combine(_dir, "state.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
        _jobs = new BackupJobStore(_db);
        _policies = new BackupPolicyStore(_db);
        _outbox = new OutboxStore(_db);
        var backupOptions = Options.Create(new BackupOptions { RetryBaseSeconds = 60, RetryMaxMinutes = 10 });
        var paths = new BackupPaths(Options.Create(new ControlAgentOptions { DataDirectory = _dir }), backupOptions);
        var identity = new AgentIdentity { AgentId = "agent-1", ServerId = "srv-1", MachineId = "m", MachineName = "n", AgentVersion = "1.0.0" };
        var events = new JobEventRecorder(_jobs, identity.AgentId, TimeProvider.System, TimeSpan.FromSeconds(30));

        _runner = new BackupJobRunner(_jobs, _policies, events, _plane, new FakeGovernor(), _worker, paths, _outbox, identity,
            backupOptions, TimeProvider.System, NullLogger<BackupJobRunner>.Instance);
        _policies.Upsert(BackupTestData.Policy(), DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task GrantedAndSuccessful_EndsSucceeded_EmitsOrderedEvents_RecordsSuccess()
    {
        _worker.Stages = [BackupJobStates.Scanning, BackupJobStates.Transferring, BackupJobStates.Verifying];
        _worker.Result = new WorkerResult
        {
            State = BackupJobStates.Succeeded,
            Summary = new JobResultSummary { VerificationState = VerificationStates.Verified, SourceBytes = 5000, Consistency = ConsistencyLevels.ApplicationConsistent }
        };
        var job = Create("j-ok");

        await _runner.RunAsync(job, CancellationToken.None);

        var stored = _jobs.Get("j-ok")!;
        Assert.Equal(BackupJobStates.Succeeded, stored.State);
        Assert.Null(stored.LeaseId); // cleared at the end

        var events = Events();
        Assert.Equal(events.Select(e => e.Sequence).OrderBy(s => s), events.Select(e => e.Sequence));
        Assert.Equal(BackupEventTypes.Started, events[0].EventType);
        Assert.Equal(BackupEventTypes.Verified, events[^1].EventType);
        Assert.Contains(events, e => e.Stage == BackupJobStates.Transferring);
        Assert.All(events, e => Assert.Equal(42, e.FencingToken ?? 42));
        Assert.All(events, e => Assert.Equal(BackupSchema.Version, e.SchemaVersion));

        var state = _policies.GetScheduleState("pol-1");
        Assert.NotNull(state.LastSuccessAt);
        Assert.NotNull(state.LastVerifiedAt);
        Assert.Equal(5000, state.LastSourceBytes);
        Assert.True(_plane.Released);
    }

    [Fact]
    public async Task EventsNeverContainGrantSecrets()
    {
        _worker.Result = new WorkerResult { State = BackupJobStates.Succeeded };
        await _runner.RunAsync(Create("j-secret"), CancellationToken.None);

        foreach (var item in _outbox.PeekHead(100))
        {
            Assert.DoesNotContain("unit-test-only", item.Payload ?? "");
            Assert.DoesNotContain("ya29.", item.Payload ?? "");
        }
    }

    [Fact]
    public async Task AdmissionDeferred_KeepsJobQueued_UntilNotBefore()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(20);
        _plane.Admission = new CallResult<AdmissionResponse>(CallStatus.Ok,
            new AdmissionResponse { Decision = AdmissionDecisions.Deferred, NotBefore = notBefore, JitterWindowSeconds = 0, Reason = "pool busy" }, null, "OK", HttpStatusCode.OK);

        await _runner.RunAsync(Create("j-defer"), CancellationToken.None);

        var stored = _jobs.Get("j-defer")!;
        Assert.Equal(BackupJobStates.Queued, stored.State);
        Assert.Equal(0, stored.Attempt);
        Assert.Equal(BackupReasonCodes.AdmissionDeferred, stored.ReasonCode);
        Assert.True(stored.NotBefore >= notBefore.AddSeconds(-1));
        Assert.False(_worker.Ran);
    }

    [Fact]
    public async Task ControlPlaneUnreachable_DefersWithoutConsumingAnAttempt()
    {
        _plane.Admission = new CallResult<AdmissionResponse>(CallStatus.Transient, null, TimeSpan.FromMinutes(2), "Network error", null);

        await _runner.RunAsync(Create("j-offline"), CancellationToken.None);

        var stored = _jobs.Get("j-offline")!;
        Assert.Equal(BackupJobStates.Queued, stored.State);
        Assert.Equal(0, stored.Attempt);
        Assert.Equal(BackupReasonCodes.ControlPlaneUnreachable, stored.ReasonCode);
        Assert.True(stored.NotBefore > DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task AdmissionDenied_Fails()
    {
        _plane.Admission = new CallResult<AdmissionResponse>(CallStatus.Ok, new AdmissionResponse { Decision = AdmissionDecisions.Denied, Reason = "no entitlement" }, null, "OK", HttpStatusCode.OK);
        await _runner.RunAsync(Create("j-denied"), CancellationToken.None);
        Assert.Equal(BackupReasonCodes.AdmissionDenied, _jobs.Get("j-denied")!.ReasonCode);
        Assert.Equal(BackupJobStates.Failed, _jobs.Get("j-denied")!.State);
    }

    [Fact]
    public async Task TransientWorkerFailure_Retries_WithBackoff()
    {
        _worker.Result = new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.StorageUnavailable, Message = "503" };

        await _runner.RunAsync(Create("j-retry"), CancellationToken.None);

        var stored = _jobs.Get("j-retry")!;
        Assert.Equal(BackupJobStates.Retrying, stored.State);
        Assert.Equal(1, stored.Attempt);
        Assert.True(stored.NotBefore > DateTimeOffset.UtcNow);
        Assert.Contains(Events(), e => e.EventType == BackupEventTypes.Retrying);
    }

    [Fact]
    public async Task TransientFailure_OnLastAttempt_FailsWithRetriesExhaustedMessage()
    {
        _worker.Result = new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.StorageUnavailable, Message = "503" };
        var job = Create("j-exhaust");
        _jobs.Transition(job.JobId, new JobTransition { State = BackupJobStates.Retrying, IncrementAttempt = true }, DateTimeOffset.UtcNow);
        _jobs.Transition(job.JobId, new JobTransition { State = BackupJobStates.Retrying, IncrementAttempt = true }, DateTimeOffset.UtcNow);

        await _runner.RunAsync(_jobs.Get(job.JobId)!, CancellationToken.None);

        var stored = _jobs.Get("j-exhaust")!;
        Assert.Equal(BackupJobStates.Failed, stored.State);
        Assert.Contains("Retries exhausted", stored.Message);
    }

    [Fact]
    public async Task PermanentWorkerFailure_FailsImmediately()
    {
        _worker.Result = new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.RepositoryPasswordInvalid, Message = "wrong password" };
        await _runner.RunAsync(Create("j-perm"), CancellationToken.None);
        Assert.Equal(BackupJobStates.Failed, _jobs.Get("j-perm")!.State);
    }

    [Fact]
    public async Task GrantForAnotherRepository_IsRefused_AndLeaseReleased()
    {
        _plane.Grant = new CallResult<StorageGrant>(CallStatus.Ok, BackupTestData.Grant(repositoryId: "someone-else"), null, "OK", HttpStatusCode.OK);

        await _runner.RunAsync(Create("j-wrong-repo"), CancellationToken.None);

        Assert.Equal(BackupJobStates.Failed, _jobs.Get("j-wrong-repo")!.State);
        Assert.Equal(BackupReasonCodes.StorageGrantFailed, _jobs.Get("j-wrong-repo")!.ReasonCode);
        Assert.False(_worker.Ran);
        Assert.True(_plane.Released);
    }

    [Fact]
    public async Task CancelRequestedBeforeStart_EndsCancelled_WithoutAdmission()
    {
        var job = Create("j-cancel");
        _jobs.RequestCancel(job.JobId);

        await _runner.RunAsync(_jobs.Get(job.JobId)!, CancellationToken.None);

        Assert.Equal(BackupJobStates.Cancelled, _jobs.Get("j-cancel")!.State);
        Assert.Equal(0, _plane.AdmissionCalls);
    }

    [Fact]
    public async Task MissingPolicy_Fails()
    {
        _policies.Remove("pol-1");
        await _runner.RunAsync(Create("j-nopolicy"), CancellationToken.None);
        Assert.Equal(BackupReasonCodes.PolicyNotFound, _jobs.Get("j-nopolicy")!.ReasonCode);
    }

    [Fact]
    public async Task PartialResult_IsReportedAsPartial_NotSuccess()
    {
        _worker.Result = new WorkerResult { State = BackupJobStates.Partial, ReasonCode = BackupReasonCodes.ApplicationNotQuiesced, Message = "Tally running" };
        await _runner.RunAsync(Create("j-partial"), CancellationToken.None);
        var stored = _jobs.Get("j-partial")!;
        Assert.Equal(BackupJobStates.Partial, stored.State);
        Assert.Equal(BackupReasonCodes.ApplicationNotQuiesced, stored.ReasonCode);
    }

    [Fact]
    public async Task RestoreJob_UsesRestoreEventRoute()
    {
        _worker.Result = new WorkerResult { State = BackupJobStates.Succeeded };
        var spec = new JobSpecData
        {
            Restore = new RestoreExecuteParameters
            {
                RestoreJobId = "r-1", RecoveryPointId = "rp-1", RepositoryId = "repo-1", Engine = BackupEngineNames.Kopia,
                EngineSnapshotId = "abc123", SourceRoot = @"D:\Tally\Data", TargetKind = RestoreTargetKinds.Alternate, TargetPath = @"D:\Restore"
            }
        };
        _jobs.CreateIfAbsent(new BackupJobRecord
        {
            JobId = "r-1", Kind = BackupJobKinds.Restore, IdempotencyKey = "restore:r-1", CorrelationId = "c", State = BackupJobStates.Queued,
            NotBefore = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, RepositoryId = "repo-1", Engine = BackupEngineNames.Kopia, SpecJson = spec.ToJson()
        });

        await _runner.RunAsync(_jobs.Get("r-1")!, CancellationToken.None);

        Assert.Equal(BackupJobStates.Succeeded, _jobs.Get("r-1")!.State);
        Assert.All(_outbox.PeekHead(100), i => Assert.Equal(ControlPlaneRoutes.RestoreEvents("r-1"), i.Route));
        Assert.Equal(BackupJobKinds.Restore, _worker.LastSpec!.Kind);
    }

    private BackupJobRecord Create(string id) => _jobs.CreateIfAbsent(new BackupJobRecord
    {
        JobId = id,
        Kind = BackupJobKinds.Backup,
        PolicyId = "pol-1",
        PolicyVersionId = "ver-1",
        IdempotencyKey = "k-" + id,
        CorrelationId = "corr-" + id,
        State = BackupJobStates.Queued,
        NotBefore = DateTimeOffset.UtcNow,
        MaxAttempts = 3,
        RepositoryId = "repo-1",
        Engine = BackupEngineNames.Kopia,
        CreatedAt = DateTimeOffset.UtcNow
    }).Job;

    private List<BackupJobEvent> Events() =>
        _outbox.PeekHead(200).Where(i => i.Payload is not null)
            .Select(i => JsonSerializer.Deserialize<BackupJobEvent>(i.Payload!, CloudOrc.ControlAgent.Transport.BackendApiClient.SnakeCaseJson)!)
            .ToList();

    private sealed class FakeControlPlane : IBackupControlPlane
    {
        public CallResult<AdmissionResponse> Admission { get; set; } = new(CallStatus.Ok, new AdmissionResponse
        {
            Decision = AdmissionDecisions.Granted,
            Lease = new AdmissionLease { LeaseId = "lease-1", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30), FencingToken = 42, MaxParallel = 1 }
        }, null, "OK", HttpStatusCode.OK);

        public CallResult<StorageGrant> Grant { get; set; } = new(CallStatus.Ok, BackupTestData.Grant(), null, "OK", HttpStatusCode.OK);

        public int AdmissionCalls { get; private set; }

        public bool Released { get; private set; }

        public bool IsConfigured => true;

        public Task<CallResult<AdmissionResponse>> RequestAdmissionAsync(AdmissionRequest request, CancellationToken cancellationToken)
        {
            AdmissionCalls++;
            return Task.FromResult(Admission);
        }

        public Task<CallResult<AdmissionLease>> RenewLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CallResult<AdmissionLease>(CallStatus.Ok, Admission.Value!.Lease, null, "OK", HttpStatusCode.OK));

        public Task ReleaseLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken)
        {
            Released = true;
            return Task.CompletedTask;
        }

        public Task<CallResult<StorageGrant>> RequestGrantAsync(StorageGrantRequest request, CancellationToken cancellationToken) => Task.FromResult(Grant);
    }

    private sealed class FakeWorkerHost : IWorkerHost
    {
        public IReadOnlyList<string> Stages { get; set; } = [];

        public WorkerResult Result { get; set; } = new() { State = BackupJobStates.Succeeded };

        public bool Ran { get; private set; }

        public WorkerJobSpec? LastSpec { get; private set; }

        public Task<WorkerResult> RunAsync(WorkerJobSpec spec, ResourceBudget budget, IReadOnlyCollection<string> volumeRoots, bool maintenance,
            IWorkerCallbacks callbacks, Func<ResourceSample?, Task>? onResourceSample, CancellationToken cancellationToken)
        {
            Ran = true;
            LastSpec = spec;
            foreach (var stage in Stages)
            {
                callbacks.OnStage(stage);
                callbacks.OnProgress(new JobProgress { Percent = 50 }, 1000, null);
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeGovernor : IResourceGovernor
    {
        public ResourceBudget ResolveBudget(string? profile, AdmissionLease? lease) =>
            ResourceProfiles.ApplyLease(ResourceProfiles.Resolve(profile, 2, 4L << 30), lease);

        public GovernorSession CreateSession(ResourceBudget budget, IReadOnlyCollection<string> volumeRoots, bool maintenance) =>
            throw new NotSupportedException("The fake worker host never creates a session.");
    }
}
