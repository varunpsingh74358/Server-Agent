using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Health;

namespace CloudOrc.ControlAgent.Health;

/// <summary>
/// Thread-safe in-memory record of what the Control Agent's internal workers are doing
/// right now. Both the detection and processing background services touch this on every
/// loop iteration; <see cref="HealthPipeServer"/> reads a consistent snapshot of it to
/// answer the Watchdog Agent. This is a deliberate, narrowly-scoped piece of shared
/// mutable state - it exists precisely so health can be reported, not as general-purpose
/// global state.
/// </summary>
public sealed class ControlAgentHealthState
{
    private readonly Lock _sync = new();

    private DateTimeOffset _lastDetectionHeartbeatAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastProcessingHeartbeatAt = DateTimeOffset.UtcNow;
    private string? _currentCommandId;
    private CommandStatus? _currentCommandStatus;
    private long _processedCount;
    private long _failedCount;
    private BackendConnectionState _backendConnectionState = BackendConnectionState.Disabled;
    private readonly Dictionary<string, BackendConnectionState> _targetConnectionStates = new(StringComparer.OrdinalIgnoreCase);

    public void TouchDetection()
    {
        lock (_sync)
        {
            _lastDetectionHeartbeatAt = DateTimeOffset.UtcNow;
        }
    }

    public void TouchProcessing()
    {
        lock (_sync)
        {
            _lastProcessingHeartbeatAt = DateTimeOffset.UtcNow;
        }
    }

    public void SetCurrentCommand(string commandId, CommandStatus status)
    {
        lock (_sync)
        {
            _currentCommandId = commandId;
            _currentCommandStatus = status;
        }
    }

    public void ClearCurrentCommand()
    {
        lock (_sync)
        {
            _currentCommandId = null;
            _currentCommandStatus = null;
        }
    }

    public void RecordCompletion(bool failed)
    {
        lock (_sync)
        {
            _processedCount++;
            if (failed)
            {
                _failedCount++;
            }
        }
    }

    /// <summary>
    /// Records the current backend connectivity state for reporting purposes only. This
    /// must never influence <see cref="Snapshot"/>'s HEALTHY/DEGRADED computation - a
    /// disconnected backend is not the same thing as an unhealthy Control Agent. See
    /// docs/ARCHITECTURE.md. Equivalent to calling the two-argument overload for the
    /// "default" (primary) connection.
    /// </summary>
    public void SetBackendConnectionState(BackendConnectionState state) =>
        SetBackendConnectionState("default", state);

    /// <summary>
    /// Same as <see cref="SetBackendConnectionState(BackendConnectionState)"/> but scoped
    /// to one named backend target, for agents maintaining more than one simultaneous
    /// backend connection (e.g. a production connection plus a local development tunnel).
    /// The aggregate state exposed on <see cref="ControlAgentHealthSnapshot"/> reflects the
    /// best state across all known targets - Connected if any target is connected,
    /// otherwise Connecting/Reconnecting if any target is mid-attempt, otherwise
    /// Disconnected, otherwise Disabled - so existing single-target callers and the
    /// wire-level snapshot contract are unaffected by adding more targets.
    /// </summary>
    public void SetBackendConnectionState(string targetName, BackendConnectionState state)
    {
        lock (_sync)
        {
            _targetConnectionStates[targetName] = state;
            _backendConnectionState = ComputeAggregateBackendConnectionState();
        }
    }

    private BackendConnectionState ComputeAggregateBackendConnectionState()
    {
        if (_targetConnectionStates.Count == 0)
        {
            return BackendConnectionState.Disabled;
        }

        if (_targetConnectionStates.Values.Any(s => s == BackendConnectionState.Connected))
        {
            return BackendConnectionState.Connected;
        }

        if (_targetConnectionStates.Values.Any(s => s == BackendConnectionState.Connecting))
        {
            return BackendConnectionState.Connecting;
        }

        if (_targetConnectionStates.Values.Any(s => s == BackendConnectionState.Reconnecting))
        {
            return BackendConnectionState.Reconnecting;
        }

        if (_targetConnectionStates.Values.Any(s => s == BackendConnectionState.Disconnected))
        {
            return BackendConnectionState.Disconnected;
        }

        return BackendConnectionState.Disabled;
    }

    public ControlAgentHealthSnapshot Snapshot(TimeSpan heartbeatTimeout)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var detectionAlive = now - _lastDetectionHeartbeatAt < heartbeatTimeout;
            var processingAlive = now - _lastProcessingHeartbeatAt < heartbeatTimeout;

            return new ControlAgentHealthSnapshot
            {
                Status = detectionAlive && processingAlive ? "HEALTHY" : "DEGRADED",
                DetectionWorkerAlive = detectionAlive,
                ProcessingWorkerAlive = processingAlive,
                LastDetectionActivityAt = _lastDetectionHeartbeatAt,
                LastProcessingActivityAt = _lastProcessingHeartbeatAt,
                CurrentCommandId = _currentCommandId,
                CurrentCommandStatus = _currentCommandStatus,
                ProcessedCount = _processedCount,
                FailedCount = _failedCount,
                BackendConnectionState = _backendConnectionState,
                GeneratedAt = now
            };
        }
    }
}
