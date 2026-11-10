using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.ControlAgent.ResourceGovernor;

public enum JobPriority
{
    Idle,
    BelowNormal,
    Normal
}

/// <summary>
/// The OS + engine budget for one job. OS caps (CPU, memory, disk IO) are enforced by a Windows
/// Job Object around the worker and every engine process it spawns - so the limit holds for the
/// "child Kopia/Restic process" too, not just the parent (§7). Engine knobs are passed to the
/// worker so the Go engines stay inside the cap gracefully instead of being killed by it.
/// </summary>
public sealed record ResourceBudget
{
    public required string Profile { get; init; }

    /// <summary>Hard CPU cap as % of the whole machine (Job Object CPU rate control, hard cap).</summary>
    public required double MaxCpuPercent { get; init; }

    /// <summary>Hard memory cap for the whole job tree (Job Object job memory limit).</summary>
    public required long MaxMemoryBytes { get; init; }

    /// <summary>Disk read/write bandwidth cap per source volume (bytes/s); null = uncapped.</summary>
    public long? MaxDiskBytesPerSecond { get; init; }

    /// <summary>Upload cap (bytes/s); null = only what the admission lease says.</summary>
    public long? MaxUploadBytesPerSecond { get; init; }

    public required int Parallelism { get; init; }
    public required int GoMaxProcs { get; init; }
    public required JobPriority Priority { get; init; }

    /// <summary>Whether the adaptive controller may go all the way to the near-pause level (Eco).</summary>
    public required bool AllowAdaptivePause { get; init; }

    /// <summary>Whether an active protected application alone raises the throttle.</summary>
    public required bool YieldToProtectedApps { get; init; }

    /// <summary>Go runtime soft memory limit: ~60% of the hard cap, leaving room for rclone + overhead.</summary>
    public int GoMemoryLimitMb => (int)Math.Max(64, MaxMemoryBytes * 0.6 / (1024 * 1024));

    public int CacheMb => (int)Math.Clamp(MaxMemoryBytes / (1024 * 1024) / 2, 64, 1024);
}

/// <summary>
/// The three §7 profiles. Numbers are conservative starting points - §7 requires them to be
/// confirmed by the benchmark (2 vCPU / 4 GB with active Tally users etc.) before GA.
/// </summary>
public static class ResourceProfiles
{
    private const long Mb = 1024L * 1024L;

    public static ResourceBudget Resolve(string? profile, int processorCount, long totalMemoryBytes)
    {
        var cores = Math.Max(1, processorCount);
        var memoryCeiling = totalMemoryBytes > 0 ? totalMemoryBytes : 4096 * Mb;

        return (profile ?? ResourceProfileNames.Eco).ToLowerInvariant() switch
        {
            // "Minimal workers, background priority, low I/O/network budget, adaptive pause"
            // Target: 2 vCPU / 4 GB VM with active Tally users.
            ResourceProfileNames.Eco => new ResourceBudget
            {
                Profile = ResourceProfileNames.Eco,
                MaxCpuPercent = cores <= 2 ? 35 : 25,
                MaxMemoryBytes = Math.Min(768 * Mb, memoryCeiling / 5),
                MaxDiskBytesPerSecond = 25 * Mb,
                MaxUploadBytesPerSecond = 2_500_000, // ~20 Mbit/s unless the lease says less
                Parallelism = 1,
                GoMaxProcs = 1,
                Priority = JobPriority.Idle,
                AllowAdaptivePause = true,
                YieldToProtectedApps = true
            },

            // "Bounded moderate parallelism, adaptive compression and bandwidth" - general servers.
            ResourceProfileNames.Balanced => new ResourceBudget
            {
                Profile = ResourceProfileNames.Balanced,
                MaxCpuPercent = 50,
                MaxMemoryBytes = Math.Min(1536 * Mb, memoryCeiling / 4),
                MaxDiskBytesPerSecond = 80 * Mb,
                MaxUploadBytesPerSecond = null,
                Parallelism = Math.Clamp(cores / 2, 1, 4),
                GoMaxProcs = Math.Clamp(cores / 2, 1, 4),
                Priority = JobPriority.BelowNormal,
                AllowAdaptivePause = false,
                YieldToProtectedApps = false
            },

            // "More parallel work within measured disk/CPU/memory limits" - large servers, maintenance windows.
            ResourceProfileNames.Throughput => new ResourceBudget
            {
                Profile = ResourceProfileNames.Throughput,
                MaxCpuPercent = 85,
                MaxMemoryBytes = Math.Min(4096 * Mb, memoryCeiling / 3),
                MaxDiskBytesPerSecond = null,
                MaxUploadBytesPerSecond = null,
                Parallelism = Math.Clamp(cores - 1, 1, 16),
                GoMaxProcs = Math.Clamp(cores - 1, 1, 16),
                Priority = JobPriority.Normal,
                AllowAdaptivePause = false,
                YieldToProtectedApps = false
            },

            _ => Resolve(ResourceProfileNames.Eco, processorCount, totalMemoryBytes)
        };
    }

    /// <summary>Applies the admission lease limits (§12 GRANTED: max_parallel, max_upload_mbps) - the stricter value wins.</summary>
    public static ResourceBudget ApplyLease(ResourceBudget budget, AdmissionLease? lease)
    {
        if (lease is null)
        {
            return budget;
        }

        var parallel = lease.MaxParallel is > 0 ? Math.Min(budget.Parallelism, lease.MaxParallel.Value) : budget.Parallelism;
        long? upload = budget.MaxUploadBytesPerSecond;
        if (lease.MaxUploadMbps is > 0)
        {
            var leaseBytes = (long)(lease.MaxUploadMbps.Value * 1_000_000 / 8);
            upload = upload is null ? leaseBytes : Math.Min(upload.Value, leaseBytes);
        }

        return budget with { Parallelism = parallel, GoMaxProcs = Math.Min(budget.GoMaxProcs, Math.Max(1, parallel)), MaxUploadBytesPerSecond = upload };
    }
}
