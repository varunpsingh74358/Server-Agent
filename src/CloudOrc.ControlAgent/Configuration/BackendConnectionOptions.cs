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

    /// <summary>
    /// Optional random jitter (0 to this many milliseconds, added on top of the
    /// calculated exponential delay) to avoid many agents reconnecting in lockstep after
    /// a shared backend outage. Defaults to 0 (no jitter, exact deterministic delays) to
    /// preserve the precise, documented backoff behavior this project already relies on
    /// for local testing; set this above 0 for a large production fleet.
    /// </summary>
    public int ReconnectJitterMaxMilliseconds { get; set; }

    public int HeartbeatIntervalSeconds { get; set; } = 15;

    public int TelemetryIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Extra backend connections to maintain simultaneously alongside the primary
    /// Url/Enabled connection above - e.g. a real production backend plus a local
    /// tunnel (ngrok or similar) for development. Each entry opens its own independent
    /// WebSocket connection with its own reconnect loop and optional credential; every
    /// one of them can receive COMMAND messages, and every outgoing
    /// HEARTBEAT/TELEMETRY/COMMAND_RESULT/STATUS message is broadcast to all of them.
    /// ConnectTimeoutSeconds, the reconnect settings, and the heartbeat/telemetry
    /// intervals above are shared by every target - only Url/Credential/
    /// DevelopmentAllowInsecureWs differ per entry. Empty by default, so an existing
    /// single-backend configuration is completely unaffected.
    /// </summary>
    public List<BackendTargetOptions> AdditionalTargets { get; set; } = [];
}

/// <summary>
/// One extra backend WebSocket target beyond the primary Url/Enabled connection declared
/// directly on <see cref="BackendConnectionOptions"/>. See
/// <see cref="BackendConnectionOptions.AdditionalTargets"/>.
/// </summary>
public sealed class BackendTargetOptions
{
    /// <summary>
    /// Unique label for this connection, used in logs and internal health tracking.
    /// "default" is reserved for the primary connection above and cannot be reused here.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Per-target switch, defaulting to true - set false to keep an entry in config without connecting to it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>e.g. a local tunnel URL such as <c>wss://your-tunnel-host/agent</c> for development.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Must be explicitly true to allow an insecure <c>ws://</c> URL for this target,
    /// exactly like <see cref="BackendConnectionOptions.DevelopmentAllowInsecureWs"/> does
    /// for the primary connection.
    /// </summary>
    public bool DevelopmentAllowInsecureWs { get; set; }

    /// <summary>
    /// Optional bearer credential for this specific target. Unlike the primary
    /// connection's credential (always the enrollment-issued one), this is read directly
    /// from configuration - appropriate for a local/dev backend that issues its own test
    /// credential, or requires none at all (leave empty). Never commit a real credential
    /// here in a tracked config file - supply it via an environment variable
    /// (e.g. <c>BackendConnection__AdditionalTargets__0__Credential</c>) or another
    /// untracked, out-of-repo source instead.
    /// </summary>
    public string? Credential { get; set; }
}
