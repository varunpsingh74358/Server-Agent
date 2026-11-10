namespace CloudOrc.ControlAgent.Configuration;

/// <summary>
/// "Backup" section of appsettings.json - the backup capability host inside Agent Core
/// (CO-DEV-BKP-001 §3 Agent Core / Worker, §7 governor, §8 resilience, §13 bounded state).
/// Every value has a safe default; nothing here is a secret or a backend address.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Master switch for the backup capability (also gates the "backup.v1" HELLO capability).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Full path to CloudOrc.BackupWorker.exe. Default: ..\BackupWorker\ next to the Control Agent folder.</summary>
    public string? WorkerExecutablePath { get; set; }

    /// <summary>Folder with kopia.exe / restic.exe / rclone.exe + engines.manifest.json. Default: ..\Engines\.</summary>
    public string? EngineDirectory { get; set; }

    /// <summary>Overrides the HTTPS control-plane base URL (normally from enrollment, else derived from the WSS URL).</summary>
    public string? ApiBaseUrl { get; set; }

    public int SchedulerTickSeconds { get; set; } = 30;
    public int ControlPlaneTimeoutSeconds { get; set; } = 30;

    /// <summary>Min seconds between progress events for one job (state changes are always sent immediately).</summary>
    public int ProgressEventIntervalSeconds { get; set; } = 30;

    /// <summary>Retry backoff for transient job failures (bounded exponential full jitter, §8).</summary>
    public int RetryBaseSeconds { get; set; } = 120;
    public int RetryMaxMinutes { get; set; } = 120;

    /// <summary>How long to wait before asking again when the control plane is unreachable.</summary>
    public int ControlPlaneUnreachableRetryMinutes { get; set; } = 5;

    /// <summary>Renew a storage grant this many minutes before it expires.</summary>
    public int GrantRenewLeadMinutes { get; set; } = 10;

    /// <summary>Upper bound on the persistent engine cache across repositories (§6 bounded caches).</summary>
    public int MaxEngineCacheMb { get; set; } = 2048;

    /// <summary>Hard ceiling for one job (stall protection; a timed-out job is retried and resumes).</summary>
    public int MaxJobHours { get; set; } = 24;

    public int InboxRetentionDays { get; set; } = 30;
    public int JobRetentionDays { get; set; } = 90;
    public int DeadOutboxRetentionDays { get; set; } = 7;
    public int MaxOutboxRows { get; set; } = 20_000;

    public ResourceGovernorOptions Governor { get; set; } = new();

    public CoreBudgetOptions CoreBudget { get; set; } = new();
}

/// <summary>Soft budget for Agent Core itself (§7 parent budget, "Idle Core target ≤50 MB").</summary>
public sealed class CoreBudgetOptions
{
    public bool Enabled { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 30;
    public int SampleIntervalSeconds { get; set; } = 60;

    /// <summary>When idle and above this working set, Core compacts and trims itself.</summary>
    public int IdleWorkingSetTargetMb { get; set; } = 50;

    /// <summary>Breach threshold for private bytes (logged + flagged; Core is never killed).</summary>
    public int MaxPrivateMb { get; set; } = 200;

    /// <summary>Breach threshold for Core's own average CPU over a sample interval (% of machine).</summary>
    public double MaxCpuPercent { get; set; } = 10;
}

/// <summary>Thresholds for the adaptive part of the Resource Governor (§7).</summary>
public sealed class ResourceGovernorOptions
{
    public int SampleIntervalSeconds { get; set; } = 5;

    /// <summary>Disk latency (ms, PhysicalDisk Avg. Disk sec/Transfer) above which reads are slowed.</summary>
    public double DiskLatencyHighMs { get; set; } = 25;

    /// <summary>Disk latency below which the throttle may be relaxed again.</summary>
    public double DiskLatencyLowMs { get; set; } = 10;

    public double SystemCpuHighPercent { get; set; } = 85;
    public double SystemCpuLowPercent { get; set; } = 60;
    public double MinAvailableMemoryMb { get; set; } = 400;

    /// <summary>Consecutive healthy samples needed before stepping the throttle back down by one level.</summary>
    public int RecoverAfterHealthySamples { get; set; } = 6;

    /// <summary>
    /// Processes whose presence means "an active user application is running" - for Eco this
    /// alone raises the throttle (the "active Tally users" case in §7). Names without .exe.
    /// </summary>
    public List<string> ProtectedProcessNames { get; set; } = ["tally"];
}
