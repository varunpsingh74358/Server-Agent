namespace CloudOrc.ControlAgent.Transport;

/// <summary>
/// Thread-safe view of the agent↔backend link for the local status UI and HELLO: last contact,
/// human retry status, authorization health. Kept separate from ControlAgentHealthState on
/// purpose - a disconnected backend never makes the Control Agent itself "unhealthy".
/// </summary>
public sealed class BackendLinkState
{
    private readonly Lock _sync = new();
    private DateTimeOffset? _lastContactAt;
    private string? _retryStatus;
    private AuthorizationHealth _authorization = AuthorizationHealth.Ok;
    private bool _connected;
    private bool _stopped;

    public event Action? ReconnectRequested;

    public DateTimeOffset? LastContactAt { get { lock (_sync) { return _lastContactAt; } } }

    public string? RetryStatus { get { lock (_sync) { return _retryStatus; } } }

    public AuthorizationHealth Authorization { get { lock (_sync) { return _authorization; } } }

    public bool IsConnected { get { lock (_sync) { return _connected; } } }

    /// <summary>True when reconnecting was stopped because authorization was revoked (§8).</summary>
    public bool IsStopped { get { lock (_sync) { return _stopped; } } }

    public void MarkContact(DateTimeOffset now)
    {
        lock (_sync)
        {
            _lastContactAt = now;
        }
    }

    public void MarkConnected(DateTimeOffset now)
    {
        lock (_sync)
        {
            _connected = true;
            _stopped = false;
            _lastContactAt = now;
            _retryStatus = null;
            _authorization = AuthorizationHealth.Ok;
        }
    }

    public void MarkDisconnected(string? retryStatus, AuthorizationHealth authorization, bool stopped)
    {
        lock (_sync)
        {
            _connected = false;
            _retryStatus = retryStatus;
            _authorization = authorization;
            _stopped = stopped;
        }
    }

    /// <summary>Local "Retry" (tray) - wakes the reconnect loop now, even if it had stopped.</summary>
    public void RequestReconnect() => ReconnectRequested?.Invoke();
}
