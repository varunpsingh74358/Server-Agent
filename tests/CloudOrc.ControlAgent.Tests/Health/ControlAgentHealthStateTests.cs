using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Health;

namespace CloudOrc.ControlAgent.Tests.Health;

public class ControlAgentHealthStateTests
{
    [Fact]
    public void Snapshot_BeforeAnyHeartbeat_ReportsAliveWithinTimeout()
    {
        var state = new ControlAgentHealthState();

        var snapshot = state.Snapshot(TimeSpan.FromSeconds(30));

        Assert.True(snapshot.DetectionWorkerAlive);
        Assert.True(snapshot.ProcessingWorkerAlive);
        Assert.Equal("HEALTHY", snapshot.Status);
    }

    [Fact]
    public void Snapshot_WithZeroTimeout_ReportsDegraded()
    {
        var state = new ControlAgentHealthState();

        // A heartbeat timeout of zero means "already stale" the instant it's evaluated.
        var snapshot = state.Snapshot(TimeSpan.Zero);

        Assert.False(snapshot.DetectionWorkerAlive);
        Assert.False(snapshot.ProcessingWorkerAlive);
        Assert.Equal("DEGRADED", snapshot.Status);
    }

    [Fact]
    public void SetCurrentCommand_IsReflectedInSnapshot()
    {
        var state = new ControlAgentHealthState();

        state.SetCurrentCommand("test-001", CommandStatus.Running);
        var snapshot = state.Snapshot(TimeSpan.FromSeconds(30));

        Assert.Equal("test-001", snapshot.CurrentCommandId);
        Assert.Equal(CommandStatus.Running, snapshot.CurrentCommandStatus);
    }

    [Fact]
    public void ClearCurrentCommand_RemovesCommandFromSnapshot()
    {
        var state = new ControlAgentHealthState();
        state.SetCurrentCommand("test-001", CommandStatus.Running);

        state.ClearCurrentCommand();
        var snapshot = state.Snapshot(TimeSpan.FromSeconds(30));

        Assert.Null(snapshot.CurrentCommandId);
        Assert.Null(snapshot.CurrentCommandStatus);
    }

    [Fact]
    public void RecordCompletion_TracksProcessedAndFailedCounts()
    {
        var state = new ControlAgentHealthState();

        state.RecordCompletion(failed: false);
        state.RecordCompletion(failed: true);
        state.RecordCompletion(failed: true);

        var snapshot = state.Snapshot(TimeSpan.FromSeconds(30));

        Assert.Equal(3, snapshot.ProcessedCount);
        Assert.Equal(2, snapshot.FailedCount);
    }
}
