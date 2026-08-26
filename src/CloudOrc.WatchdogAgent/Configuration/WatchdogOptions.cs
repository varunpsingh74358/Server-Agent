namespace CloudOrc.WatchdogAgent.Configuration;

/// <summary>
/// Root configuration section ("Watchdog" in appsettings.json) for the Watchdog Agent.
/// </summary>
public sealed class WatchdogOptions
{
    public const string SectionName = "Watchdog";

    /// <summary>
    /// Root data directory for the Watchdog's own logs.
    /// </summary>
    public string DataDirectory { get; set; } = @"C:\ProgramData\CloudOrc\WatchdogAgent";

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Name of the Windows Service the Watchdog monitors and can restart.
    /// </summary>
    public string ControlAgentServiceName { get; set; } = "CloudOrcControlAgent";

    /// <summary>
    /// Process name (no ".exe") the Watchdog looks up in the OS process table every
    /// monitoring cycle to report the Control Agent's own CPU/memory usage - distinct
    /// from <see cref="ControlAgentServiceName"/> (the Windows Service name), which is not
    /// the same string as the executable/process name.
    /// </summary>
    public string ControlAgentProcessName { get; set; } = "CloudOrc.ControlAgent";

    /// <summary>
    /// Named pipe the Control Agent's health server listens on.
    /// </summary>
    public string HealthPipeName { get; set; } = "CloudOrc.ControlAgent.Health";

    /// <summary>
    /// How often a full monitoring cycle runs.
    /// </summary>
    public int HealthCheckIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// How long to wait for the Control Agent to respond on the health pipe before
    /// treating it as unresponsive.
    /// </summary>
    public int HealthCheckTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Number of consecutive failed health checks required before recovery is attempted.
    /// A single transient failure never triggers a restart.
    /// </summary>
    public int ConsecutiveFailureThreshold { get; set; } = 3;

    /// <summary>
    /// After issuing a restart, how long to wait before checking health again.
    /// </summary>
    public int RecoveryWaitSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum number of restart attempts allowed within <see cref="RestartRateLimitWindowMinutes"/>.
    /// </summary>
    public int MaxRestartAttemptsPerWindow { get; set; } = 3;

    /// <summary>
    /// Rolling time window (minutes) over which <see cref="MaxRestartAttemptsPerWindow"/> is enforced.
    /// </summary>
    public int RestartRateLimitWindowMinutes { get; set; } = 15;

    /// <summary>
    /// Base delay applied between successive recovery attempts, doubled after each
    /// consecutive failed recovery (capped at <see cref="MaxBackoffSeconds"/>).
    /// </summary>
    public int InitialBackoffSeconds { get; set; } = 30;

    /// <summary>
    /// Upper bound for the exponential backoff delay between recovery attempts.
    /// </summary>
    public int MaxBackoffSeconds { get; set; } = 600;
}
