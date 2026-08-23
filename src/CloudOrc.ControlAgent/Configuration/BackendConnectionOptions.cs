namespace CloudOrc.ControlAgent.Configuration;

/// <summary>
/// Root configuration section ("BackendConnection" in appsettings.json) controlling the
/// agent's outbound WebSocket connection to a backend. Disabled by default - the agent
/// works exactly as before (local file mode only) unless this is explicitly turned on.
/// </summary>
public sealed class BackendConnectionOptions
{
    public const string SectionName = "BackendConnection";

    /// <summary>Master switch. When false, none of the backend/WSS code runs at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The backend WebSocket URL, e.g. <c>ws://localhost:5299/agent</c> for local testing
    /// or <c>wss://your-backend/agent</c> in production. Never hardcode a real backend
    /// domain here - this is read from configuration only.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Must be explicitly set to true to allow an insecure <c>ws://</c> URL. If
    /// <see cref="Url"/> uses <c>ws://</c> and this is false (the default), the agent
    /// refuses to start the backend connection and logs a clear configuration error
    /// instead of silently connecting insecurely.
    /// </summary>
    public bool DevelopmentAllowInsecureWs { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 10;

    public int ReconnectInitialDelaySeconds { get; set; } = 2;

    public int ReconnectMaximumDelaySeconds { get; set; } = 60;

    public int HeartbeatIntervalSeconds { get; set; } = 15;

    public int TelemetryIntervalSeconds { get; set; } = 10;
}
