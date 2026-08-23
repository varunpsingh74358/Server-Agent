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
    /// docs/ARCHITECTURE.md.
    /// </summary>
    public void SetBackendConnectionState(BackendConnectionState state)
    {
        lock (_sync)
        {
            _backendConnectionState = state;
        }
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
