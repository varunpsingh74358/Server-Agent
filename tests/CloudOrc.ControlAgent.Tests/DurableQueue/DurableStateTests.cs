using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.DurableQueue;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.DurableQueue;

public sealed class DurableStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-state-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStateDatabase _db;
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    public DurableStateTests()
    {
        _db = new AgentStateDatabase(Path.Combine(_dir, "agent-state.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Inbox_FirstDeliveryIsNew_RedeliveryWhileRunningIsInProgress_AfterCompletionReplaysResult()
    {
        var inbox = new CommandInboxStore(_db);

        Assert.Equal(InboxDisposition.New, inbox.Begin("cmd-1", "backup.run", "c1", "user@x", Now).Disposition);
        Assert.Equal(InboxDisposition.InProgress, inbox.Begin("cmd-1", "backup.run", "c1", "user@x", Now).Disposition);

        inbox.Complete("cmd-1", "{\"type\":\"COMMAND_RESULT\"}", Now);
        var replay = inbox.Begin("cmd-1", "backup.run", "c1", "user@x", Now);
        Assert.Equal(InboxDisposition.Completed, replay.Disposition);
        Assert.Equal("{\"type\":\"COMMAND_RESULT\"}", replay.ResultJson);
    }

    [Fact]
    public void Inbox_AbandonInFlight_GivesInterruptedCommandsADefiniteAnswer()
    {
        var inbox = new CommandInboxStore(_db);
        inbox.Begin("cmd-2", "backup.run", null, null, Now);

        Assert.Equal(1, inbox.AbandonInFlight("{\"failed\":true}", Now));
        Assert.Equal(InboxDisposition.Completed, inbox.Begin("cmd-2", "backup.run", null, null, Now).Disposition);
    }

    [Fact]
    public void Outbox_IsDeliveredInSequenceOrder_AndDuplicateEventIdsAreIgnored()
    {
        var outbox = new OutboxStore(_db);
        outbox.Enqueue(Item("e1"), Now);
        outbox.Enqueue(Item("e2"), Now);
        outbox.Enqueue(Item("e1"), Now); // same event id: ignored (idempotent enqueue)

        var head = outbox.PeekHead(10);
        Assert.Equal(["e1", "e2"], head.Select(i => i.EventId));
        Assert.True(head[0].Sequence < head[1].Sequence);

        outbox.MarkDelivered(head[0].Sequence);
        Assert.Equal(["e2"], outbox.PeekHead(10).Select(i => i.EventId));
    }

    [Fact]
    public void Outbox_FailedRowWaitsForItsNextAttempt_DeadRowIsSkipped()
    {
        var outbox = new OutboxStore(_db);
        outbox.Enqueue(Item("a"), Now);
        outbox.Enqueue(Item("b"), Now);
        var head = outbox.PeekHead(10);

        outbox.MarkFailed(head[0].Sequence, "HTTP 503", Now.AddMinutes(5), dead: false);
        var retried = outbox.PeekHead(10)[0];
        Assert.Equal(1, retried.Attempts);
        Assert.Equal(Now.AddMinutes(5), retried.NextAttemptAt);

        outbox.MarkFailed(head[0].Sequence, "HTTP 400", Now, dead: true);
        Assert.Equal(["b"], outbox.PeekHead(10).Select(i => i.EventId));
    }

    [Fact]
    public void Outbox_Cap_DropsOldestProgressEventsButNeverTerminalOnes()
    {
        var outbox = new OutboxStore(_db);
        outbox.Enqueue(Item("terminal-1", terminal: true), Now);
        for (var i = 0; i < 10; i++)
        {
            outbox.Enqueue(Item($"progress-{i}"), Now);
        }

        outbox.EnforceCap(5);

        var remaining = outbox.PeekHead(100).Select(i => i.EventId).ToList();
        Assert.Equal(5, remaining.Count);
        Assert.Contains("terminal-1", remaining);
        Assert.DoesNotContain("progress-0", remaining);
        Assert.Contains("progress-9", remaining);
    }

    [Fact]
    public void Jobs_CreateIfAbsent_IsIdempotentOnTheIdempotencyKey()
    {
        var jobs = new BackupJobStore(_db);
        var (first, created1) = jobs.CreateIfAbsent(Job("job-1", "sched:p1:slot1"));
        var (second, created2) = jobs.CreateIfAbsent(Job("job-2", "sched:p1:slot1"));

        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(first.JobId, second.JobId);
    }

    [Fact]
    public void Jobs_TransitionWritesStateAndEventAtomically_WithIncreasingSequence()
    {
        var jobs = new BackupJobStore(_db);
        var outbox = new OutboxStore(_db);
        jobs.CreateIfAbsent(Job("job-3", "k3"));

        void Write(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, BackupJobRecord j, long seq) =>
            OutboxStore.Insert(c, t, new OutboxItem { EventId = $"evt-{seq}", Kind = OutboxKinds.JobEvent, Route = "r", Payload = seq.ToString(), JobId = j.JobId }, Now);

        jobs.Transition("job-3", new JobTransition { State = BackupJobStates.Snapshotting, MarkStarted = true }, Now, Write);
        var final = jobs.Transition("job-3", new JobTransition { State = BackupJobStates.Succeeded, MarkEnded = true }, Now, Write);

        Assert.Equal(BackupJobStates.Succeeded, final.State);
        Assert.Equal(2, final.LastSequence);
        Assert.NotNull(final.StartedAt);
        Assert.NotNull(final.EndedAt);
        Assert.Equal(["evt-1", "evt-2"], outbox.PeekHead(10).Select(i => i.EventId));
    }

    [Fact]
    public void Jobs_TransitionWithoutEvent_DoesNotConsumeASequenceNumber()
    {
        var jobs = new BackupJobStore(_db);
        jobs.CreateIfAbsent(Job("job-4", "k4"));
        var after = jobs.Transition("job-4", new JobTransition { State = BackupJobStates.Queued }, Now);
        Assert.Equal(0, after.LastSequence);
    }

    [Fact]
    public void Jobs_NextRunnable_RespectsNotBefore_AndPrefersRestores()
    {
        var jobs = new BackupJobStore(_db);
        jobs.CreateIfAbsent(Job("later", "k-later", notBefore: Now.AddHours(1)));
        jobs.CreateIfAbsent(Job("backup", "k-backup", notBefore: Now.AddMinutes(-10)));
        jobs.CreateIfAbsent(Job("restore", "k-restore", notBefore: Now.AddMinutes(-1), kind: BackupJobKinds.Restore));

        Assert.Equal("restore", jobs.NextRunnable(Now)!.JobId);
        Assert.Equal(Now.AddMinutes(-10), jobs.EarliestPendingNotBefore());
    }

    [Fact]
    public void Jobs_InterruptedActiveJobsAreFoundForReconciliation()
    {
        var jobs = new BackupJobStore(_db);
        jobs.CreateIfAbsent(Job("running", "k-run"));
        jobs.Transition("running", new JobTransition { State = BackupJobStates.Transferring }, Now);

        Assert.Equal(["running"], jobs.GetInterrupted().Select(j => j.JobId));
    }

    [Fact]
    public void Jobs_RequestCancel_OnlyForNonTerminalJobs()
    {
        var jobs = new BackupJobStore(_db);
        jobs.CreateIfAbsent(Job("c1", "kc1"));
        Assert.True(jobs.RequestCancel("c1"));
        Assert.True(jobs.Get("c1")!.CancelRequested);

        jobs.Transition("c1", new JobTransition { State = BackupJobStates.Cancelled, MarkEnded = true }, Now);
        Assert.False(jobs.RequestCancel("c1"));
    }

    [Fact]
    public void Database_SurvivesReopen_SchemaIsNotRecreated()
    {
        var jobs = new BackupJobStore(_db);
        jobs.CreateIfAbsent(Job("persist", "kp"));
        _db.Dispose();

        using var reopened = new AgentStateDatabase(Path.Combine(_dir, "agent-state.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
        Assert.NotNull(new BackupJobStore(reopened).Get("persist"));
    }

    [Fact]
    public void PolicyStore_RoundTripsPolicyAndScheduleState()
    {
        var store = new BackupPolicyStore(_db);
        store.Upsert(CloudOrc.ControlAgent.Tests.Backup.BackupTestData.Policy(), Now);
        store.SetLastSlot("pol-1", Now);
        store.RecordSuccess("pol-1", Now, verified: true, sourceBytes: 1234);

        Assert.Equal("ver-1", store.Get("pol-1")!.PolicyVersionId);
        var state = store.GetScheduleState("pol-1");
        Assert.Equal(Now, state.LastSlot);
        Assert.Equal(Now, state.LastVerifiedAt);
        Assert.Equal(1234, state.LastSourceBytes);
        Assert.True(store.Remove("pol-1"));
        Assert.Empty(store.GetAll());
    }

    private static OutboxItem Item(string id, bool terminal = false) => new()
    {
        EventId = id,
        Kind = terminal ? OutboxKinds.JobEvent : OutboxKinds.JobProgress,
        Route = "api/v1/backup/jobs/x/events",
        Payload = "{}",
        IsTerminal = terminal
    };

    private static BackupJobRecord Job(string id, string key, DateTimeOffset? notBefore = null, string kind = BackupJobKinds.Backup) => new()
    {
        JobId = id,
        Kind = kind,
        PolicyId = "pol-1",
        IdempotencyKey = key,
        CorrelationId = "corr",
        State = BackupJobStates.Queued,
        NotBefore = notBefore ?? Now,
        CreatedAt = Now
    };
}
