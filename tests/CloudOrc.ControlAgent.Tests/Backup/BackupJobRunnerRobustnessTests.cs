using System.Net;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.ResourceGovernor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Backup;

/// <summary>Regression tests for the issues found by the independent review.</summary>
public sealed class BackupJobRunnerRobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-robust-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStateDatabase _db;
    private readonly BackupJobStore _jobs;
    private readonly BackupPolicyStore _policies;
    private readonly Plane _plane = new();
    private readonly Worker _worker = new();
    private readonly BackupJobRunner _runner;

    public BackupJobRunnerRobustnessTests()
    {
        _db = new AgentStateDatabase(Path.Combine(_dir, "s.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
        _jobs = new BackupJobStore(_db);
        _policies = new BackupPolicyStore(_db);
        var options = Options.Create(new BackupOptions { RetryBaseSeconds = 60, RetryMaxMinutes = 10 });
        var identity = new AgentIdentity { AgentId = "a", ServerId = "s", MachineId = "m", MachineName = "n", AgentVersion = "1" };
        _runner = new BackupJobRunner(_jobs, _policies, new JobEventRecorder(_jobs, "a", TimeProvider.System, TimeSpan.FromSeconds(30)),
            _plane, new Governor(), _worker, new BackupPaths(Options.Create(new ControlAgentOptions { DataDirectory = _dir }), options),
            new OutboxStore(_db), identity, options, TimeProvider.System, NullLogger<BackupJobRunner>.Instance);
        _policies.Upsert(BackupTestData.Policy(), DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private BackupJobRecord Create(string id) => _jobs.CreateIfAbsent(new BackupJobRecord
    {
        JobId = id, Kind = BackupJobKinds.Backup, PolicyId = "pol-1", PolicyVersionId = "ver-1", IdempotencyKey = "k-" + id,
        CorrelationId = "c", State = BackupJobStates.Queued, NotBefore = DateTimeOffset.UtcNow, MaxAttempts = 3,
        RepositoryId = "repo-1", Engine = BackupEngineNames.Kopia, CreatedAt = DateTimeOffset.UtcNow
    }).Job;

    [Fact]
    public async Task CancelDuringAdmission_EndsCancelled_AndNeverThrows()
    {
        _plane.BlockAdmission = true;
        using var jobCts = new CancellationTokenSource();
        var run = _runner.RunAsync(Create("j-cancel"), jobCts.Token, CancellationToken.None);
        await Task.Delay(100);
        await jobCts.CancelAsync();

        await run; // must not throw - an escaping exception would stop the whole agent host

        Assert.Equal(BackupJobStates.Cancelled, _jobs.Get("j-cancel")!.State);
    }

    [Fact]
    public async Task ServiceStopping_LeavesTheJobForReconciliation_InsteadOfCancellingIt()
    {
        using var stopping = new CancellationTokenSource();
        _worker.OnRun = () => stopping.Cancel();
        _worker.Result = new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled };

        await _runner.RunAsync(Create("j-stop"), stopping.Token, stopping.Token);

        var job = _jobs.Get("j-stop")!;
        Assert.True(BackupJobStates.IsActive(job.State), $"expected an active state for reconciliation, got {job.State}");
        Assert.Single(_jobs.GetInterrupted());
    }

    [Theory]
    [InlineData(BackupReasonCodes.SnapshotLifetimeExceeded)]
    [InlineData(BackupReasonCodes.JobTimeout)]
    public async Task LongRunsThatHitALimit_AreRetried_NotFailedForGood(string reason)
    {
        _worker.Result = new WorkerResult { State = BackupJobStates.Failed, ReasonCode = reason, Message = "limit" };
        await _runner.RunAsync(Create("j-" + reason), CancellationToken.None);
        Assert.Equal(BackupJobStates.Retrying, _jobs.Get("j-" + reason)!.State);
    }

    [Fact]
    public async Task CompletedSources_FromAnEarlierAttempt_AreHandedToTheRetry()
    {
        _worker.Checkpoint = new SnapshotRef { SourcePath = @"D:\Tally\Data", SnapshotId = "snap-1" };
        _worker.Result = new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.StorageUnavailable, Message = "503" };
        await _runner.RunAsync(Create("j-resume"), CancellationToken.None);
        Assert.Equal(BackupJobStates.Retrying, _jobs.Get("j-resume")!.State);

        _worker.Checkpoint = null;
        _worker.Result = new WorkerResult { State = BackupJobStates.Succeeded };
        await _runner.RunAsync(_jobs.Get("j-resume")!, CancellationToken.None);

        Assert.Equal("snap-1", Assert.Single(_worker.LastSpec!.CompletedSnapshots).SnapshotId);
        Assert.Equal(BackupJobStates.Succeeded, _jobs.Get("j-resume")!.State);
    }

    [Fact]
    public void TerminalJobs_CannotBeResurrected()
    {
        Create("j-final");
        _jobs.Transition("j-final", new JobTransition { State = BackupJobStates.Cancelled, MarkEnded = true }, DateTimeOffset.UtcNow);

        var after = _jobs.Transition("j-final", new JobTransition { State = BackupJobStates.Queued, NotBefore = DateTimeOffset.UtcNow }, DateTimeOffset.UtcNow);

        Assert.Equal(BackupJobStates.Cancelled, after.State);
        Assert.Null(_jobs.NextRunnable(DateTimeOffset.UtcNow.AddDays(1)));
    }

    private sealed class Plane : IBackupControlPlane
    {
        public bool BlockAdmission { get; set; }

        public bool IsConfigured => true;

        public async Task<CallResult<AdmissionResponse>> RequestAdmissionAsync(AdmissionRequest request, CancellationToken cancellationToken)
        {
            if (BlockAdmission)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // like an HTTP call cancelled by the caller
            }

            return new CallResult<AdmissionResponse>(CallStatus.Ok, new AdmissionResponse
            {
                Decision = AdmissionDecisions.Granted,
                Lease = new AdmissionLease { LeaseId = "l", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30), FencingToken = 1 }
            }, null, "OK", HttpStatusCode.OK);
        }

        public Task<CallResult<AdmissionLease>> RenewLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CallResult<AdmissionLease>(CallStatus.Ok, new AdmissionLease { LeaseId = "l", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) }, null, "OK", HttpStatusCode.OK));

        public Task ReleaseLeaseAsync(string leaseId, LeaseRenewRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CallResult<StorageGrant>> RequestGrantAsync(StorageGrantRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CallResult<StorageGrant>(CallStatus.Ok, BackupTestData.Grant(), null, "OK", HttpStatusCode.OK));
    }

    private sealed class Worker : IWorkerHost
    {
        public WorkerResult Result { get; set; } = new() { State = BackupJobStates.Succeeded };
        public SnapshotRef? Checkpoint { get; set; }
        public Action? OnRun { get; set; }
        public WorkerJobSpec? LastSpec { get; private set; }

        public Task<WorkerResult> RunAsync(WorkerJobSpec spec, ResourceBudget budget, IReadOnlyCollection<string> volumeRoots, bool maintenance,
            IWorkerCallbacks callbacks, Func<ResourceSample?, Task>? onResourceSample, CancellationToken cancellationToken)
        {
            LastSpec = spec;
            callbacks.OnStage(BackupJobStates.Transferring);
            if (Checkpoint is not null)
            {
                callbacks.OnSnapshotCompleted(Checkpoint);
            }

            OnRun?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class Governor : IResourceGovernor
    {
        public ResourceBudget ResolveBudget(string? profile, AdmissionLease? lease) => ResourceProfiles.Resolve(profile, 2, 4L << 30);

        public GovernorSession CreateSession(ResourceBudget budget, IReadOnlyCollection<string> volumeRoots, bool maintenance) => throw new NotSupportedException();
    }
}
