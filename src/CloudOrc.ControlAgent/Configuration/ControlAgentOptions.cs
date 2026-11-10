using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.ControlAgent.Configuration;

/// <summary>
/// Root configuration section ("ControlAgent" in appsettings.json) for the Control Agent.
/// </summary>
public sealed class ControlAgentOptions
{
    public const string SectionName = "ControlAgent";

    /// <summary>
    /// Whether the local JSON-file command source/result sink (commands\/results\) is
    /// active. Defaults to true. Can be run concurrently with
    /// <c>BackendConnection.Enabled</c>, or set to false to test the backend connection
    /// in isolation. At least one of the two should normally be enabled - if both are
    /// false the agent starts but never receives any work, which is logged as a warning.
    /// </summary>
    public bool LocalFileModeEnabled { get; set; } = true;

    /// <summary>
    /// Root data directory. Sub-directories (commands, processing, completed, failed,
    /// results, logs) are created underneath this at startup if they do not exist.
    /// </summary>
    public string DataDirectory { get; set; } = @"C:\ProgramData\CloudOrc\ControlAgent";

    /// <summary>
    /// How often the command detection loop scans the commands directory for new work.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// A candidate command file must have an unchanged size/timestamp for at least this
    /// long before it is considered fully written and safe to pick up. Protects against
    /// reading a JSON file while another process is still writing it.
    /// </summary>
    public int FileStabilityMilliseconds { get; set; } = 750;

    public CommandValidationOptions Validation { get; set; } = new();

    /// <summary>
    /// Whether the arbitrary-script command type (powershell-exec) is accepted at all.
    /// CO-DEV-BKP-001 §10 says "no arbitrary shell by default", and §13 says the existing script
    /// path stays available "for explicitly approved commands during migration; no big-bang
    /// cutover". It therefore defaults to true ONLY because today's provisioning still depends
    /// on it - set it to false on any endpoint that only needs typed commands (e.g. backup-only).
    /// Typed commands (backup.*, restore.*, agent.*) are unaffected by this switch.
    /// </summary>
    public bool PowerShellExecEnabled { get; set; } = true;

    /// <summary>
    /// Require a backend signature on every typed command even before a signing key was delivered
    /// at enrollment (§10). Signatures are enforced automatically once the key is known.
    /// </summary>
    public bool RequireSignedTypedCommands { get; set; }

    /// <summary>Restrict the whole data directory to SYSTEM + Administrators at startup (§10/§13).</summary>
    public bool HardenDataDirectoryAcl { get; set; } = true;

    /// <summary>Only execute local command files when the commands folder is writable by administrators/SYSTEM alone.</summary>
    public bool LocalFileRequireProtectedDirectory { get; set; } = true;

    /// <summary>
    /// Name of the named pipe the health server listens on for the Watchdog Agent.
    /// </summary>
    public string HealthPipeName { get; set; } = "CloudOrc.ControlAgent.Health";

    /// <summary>
    /// If the detection or processing worker has not recorded activity within this many
    /// seconds, the health snapshot reports it as not alive even though the process is
    /// still running.
    /// </summary>
    public int WorkerHeartbeatTimeoutSeconds { get; set; } = 30;

    public string CommandsDirectory => Path.Combine(DataDirectory, "commands");

    public string ProcessingDirectory => Path.Combine(DataDirectory, "processing");

    public string CompletedDirectory => Path.Combine(DataDirectory, "completed");

    public string FailedDirectory => Path.Combine(DataDirectory, "failed");

    public string ResultsDirectory => Path.Combine(DataDirectory, "results");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
}
