namespace CloudOrc.WatchdogAgent.ControlAgentManagement;

/// <summary>
/// One point-in-time reading of how much CPU and memory a specific process is using, as
/// seen by the Watchdog. <see cref="CpuPercent"/> is null on the very first successful
/// sample of a given process (a CPU rate needs two timestamped samples to compute a
/// delta) and on every sample after the process was not found - both are normal, not
/// errors.
/// </summary>
public sealed record ProcessResourceSnapshot
{
    public required bool IsRunning { get; init; }

    public int? ProcessId { get; init; }

    /// <summary>Percent of total machine CPU capacity (0-100, already divided by core count) used since the previous sample.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Physical RAM currently resident for this process (the Task Manager "Memory" column).</summary>
    public long? WorkingSetBytes { get; init; }

    /// <summary>Private/committed memory for this process - typically a bit higher than WorkingSetBytes.</summary>
    public long? PrivateMemoryBytes { get; init; }

    public static readonly ProcessResourceSnapshot NotRunning = new() { IsRunning = false };
}
