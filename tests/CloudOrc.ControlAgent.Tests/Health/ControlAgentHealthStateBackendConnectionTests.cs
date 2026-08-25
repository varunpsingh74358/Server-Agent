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

    [Fact]
    public void MultipleTargets_AggregateIsConnected_WhenAtLeastOneTargetIsConnected()
    {
        // The multi-backend scenario: production is still reconnecting but the local dev
        // tunnel is up - the agent as a whole should report Connected, not Reconnecting.
        var state = new ControlAgentHealthState();

        state.SetBackendConnectionState("production", BackendConnectionState.Reconnecting);
        state.SetBackendConnectionState("dev-tunnel", BackendConnectionState.Connected);

        Assert.Equal(BackendConnectionState.Connected, state.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);
    }

    [Fact]
    public void MultipleTargets_AggregateReflectsBestStateAcrossAllTargets_AsEachChanges()
    {
        var state = new ControlAgentHealthState();

        state.SetBackendConnectionState("production", BackendConnectionState.Connecting);
        state.SetBackendConnectionState("dev-tunnel", BackendConnectionState.Connecting);
        Assert.Equal(BackendConnectionState.Connecting, state.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);

        state.SetBackendConnectionState("dev-tunnel", BackendConnectionState.Connected);
        Assert.Equal(BackendConnectionState.Connected, state.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);

        // Both drop - production reconnecting beats dev-tunnel fully disconnected.
        state.SetBackendConnectionState("dev-tunnel", BackendConnectionState.Disconnected);
        state.SetBackendConnectionState("production", BackendConnectionState.Reconnecting);
        Assert.Equal(BackendConnectionState.Reconnecting, state.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);

        // Both disconnected - no target is mid-attempt any more.
        state.SetBackendConnectionState("production", BackendConnectionState.Disconnected);
        Assert.Equal(BackendConnectionState.Disconnected, state.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);
    }

    [Fact]
    public void SingleArgOverload_TargetsTheDefaultConnection_SameAsTwoArgOverloadWithDefaultName()
    {
        var viaSingleArg = new ControlAgentHealthState();
        viaSingleArg.SetBackendConnectionState(BackendConnectionState.Connected);

        var viaTwoArg = new ControlAgentHealthState();
        viaTwoArg.SetBackendConnectionState("default", BackendConnectionState.Connected);

        Assert.Equal(
            viaTwoArg.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState,
            viaSingleArg.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);
    }
}
