using CloudOrc.Agent.Contracts.Health;
using CloudOrc.ControlAgent.Health;

namespace CloudOrc.ControlAgent.Tests.Health;

/// <summary>
/// Guards the explicit architectural requirement: a disconnected/reconnecting backend
/// must never, by itself, be treated as the Control Agent being unhealthy. The Watchdog
/// only ever looks at <see cref="ControlAgentHealthSnapshot.Status"/>, so this test
/// verifies that field is driven purely by the worker heartbeats, never by backend
/// connectivity.
/// </summary>
public class ControlAgentHealthStateBackendConnectionTests
{
    [Theory]
    [InlineData(BackendConnectionState.Disabled)]
    [InlineData(BackendConnectionState.Connecting)]
    [InlineData(BackendConnectionState.Connected)]
    [InlineData(BackendConnectionState.Reconnecting)]
    [InlineData(BackendConnectionState.Disconnected)]
    public void Snapshot_WithHealthyWorkers_IsAlwaysHealthyRegardlessOfBackendState(BackendConnectionState backendState)
    {
        var state = new ControlAgentHealthState();

        state.SetBackendConnectionState(backendState);
        var snapshot = state.Snapshot(TimeSpan.FromSeconds(30));

        Assert.Equal("HEALTHY", snapshot.Status);
        Assert.True(snapshot.DetectionWorkerAlive);
        Assert.True(snapshot.ProcessingWorkerAlive);
        Assert.Equal(backendState, snapshot.BackendConnectionState);
    }

    [Fact]
    public void Snapshot_BackendDisconnected_DoesNotDegradeAnOtherwiseHealthyAgent()
    {
        // The exact scenario called out in the spec: backend goes down, local processing
        // is unaffected, so the agent must still report HEALTHY to the Watchdog.
        var state = new ControlAgentHealthState();
        state.TouchDetection();
        state.TouchProcessing();

        state.SetBackendConnectionState(BackendConnectionState.Disconnected);

        var snapshot = state.Snapshot(TimeSpan.FromSeconds(30));

        Assert.Equal("HEALTHY", snapshot.Status);
    }

    [Fact]
    public void Snapshot_WorkersActuallyStale_IsDegradedEvenIfBackendIsConnected()
    {
        // The inverse must also hold: a connected backend can never mask genuinely dead
        // workers.
        var state = new ControlAgentHealthState();
        state.SetBackendConnectionState(BackendConnectionState.Connected);

        var snapshot = state.Snapshot(TimeSpan.Zero); // zero timeout => immediately stale

        Assert.Equal("DEGRADED", snapshot.Status);
    }

    [Fact]
    public void DefaultBackendConnectionState_IsDisabled()
    {
        var state = new ControlAgentHealthState();

        var snapshot = state.Snapshot(TimeSpan.FromSeconds(30));

        Assert.Equal(BackendConnectionState.Disabled, snapshot.BackendConnectionState);
    }
}
