namespace CloudOrc.Agent.Contracts.Health;

/// <summary>
/// Connectivity state of the Control Agent's outbound connection to the backend.
/// This is informational only - it must never, by itself, be treated as a proxy for
/// whether the Control Agent as a whole is healthy. A Control Agent with
/// <see cref="Disconnected"/> backend connectivity can still be perfectly healthy
/// (its local command processing is entirely independent of this).
/// </summary>
public enum BackendConnectionState
{
    /// <summary>Backend connectivity is turned off in configuration.</summary>
    Disabled,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected
}
