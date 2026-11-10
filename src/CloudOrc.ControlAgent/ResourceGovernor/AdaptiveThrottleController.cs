using CloudOrc.ControlAgent.Configuration;

namespace CloudOrc.ControlAgent.ResourceGovernor;

/// <summary>One system pressure reading. Any metric that could not be read is null.</summary>
public sealed record PressureSample(
    double? SystemCpuPercent,
    double? DiskLatencyMs,
    double? AvailableMemoryMb,
    bool ProtectedAppActive);

/// <summary>Effective OS caps for the current throttle level.</summary>
public sealed record ThrottleSettings(int Level, double CpuPercent, long? DiskBytesPerSecond);

/// <summary>
/// Pure decision logic of the adaptive governor (no I/O, unit-testable).
///
/// §7: "On storage contention or active user slowdown, reduce scan/read rate before simply
/// limiting upload." So the levels deliberately cut DISK READ bandwidth first:
///   0 - full profile budget
///   1 - disk bandwidth halved                         (read/scan slowed first)
///   2 - disk bandwidth quartered + CPU halved
///   3 - near-pause: minimal disk + CPU (Eco "adaptive pause" only)
/// Upload speed is fixed at engine start (lease/profile) and is never the first lever.
/// Escalation is immediate on pressure; relaxing is one level per N consecutive healthy
/// samples, so the throttle does not oscillate.
/// </summary>
public sealed class AdaptiveThrottleController
{
    private const long MinimumDiskBytesPerSecond = 1024 * 1024;
    private const double MinimumCpuPercent = 5;

    private readonly ResourceBudget _budget;
    private readonly ResourceGovernorOptions _options;
    private int _healthyStreak;

    public AdaptiveThrottleController(ResourceBudget budget, ResourceGovernorOptions options)
    {
        _budget = budget;
        _options = options;
        MaxLevel = budget.AllowAdaptivePause ? 3 : 2;
    }

    public int Level { get; private set; }

    public int MaxLevel { get; }

    public ThrottleSettings Current => SettingsFor(Level);

    /// <summary>Feeds one sample; returns the (possibly new) settings and whether they changed.</summary>
    public (ThrottleSettings Settings, bool Changed) Evaluate(PressureSample sample)
    {
        var previous = Level;

        if (IsUnderPressure(sample))
        {
            _healthyStreak = 0;
            Level = Math.Min(MaxLevel, Level + 1);
        }
        else if (IsHealthy(sample))
        {
            _healthyStreak++;
            if (_healthyStreak >= Math.Max(1, _options.RecoverAfterHealthySamples) && Level > 0)
            {
                Level--;
                _healthyStreak = 0;
            }
        }
        else
        {
            // In the hysteresis band: hold the current level.
            _healthyStreak = 0;
        }

        return (SettingsFor(Level), Level != previous);
    }

    public ThrottleSettings SettingsFor(int level)
    {
        long? disk = _budget.MaxDiskBytesPerSecond;
        var cpu = _budget.MaxCpuPercent;

        // An uncapped profile still needs a reference point to throttle against once pressure appears.
        var diskReference = disk ?? 200L * 1024 * 1024;

        switch (level)
        {
            case <= 0:
                return new ThrottleSettings(0, cpu, disk);
            case 1:
                return new ThrottleSettings(1, cpu, Math.Max(MinimumDiskBytesPerSecond, diskReference / 2));
            case 2:
                return new ThrottleSettings(2, Math.Max(MinimumCpuPercent, cpu / 2), Math.Max(MinimumDiskBytesPerSecond, diskReference / 4));
            default:
                return new ThrottleSettings(3, MinimumCpuPercent, MinimumDiskBytesPerSecond);
        }
    }

    /// <summary>
    /// Where per-volume disk bandwidth control is unavailable (verified: Windows Server 2019 /
    /// build 17763 returns ERROR_INVALID_FUNCTION), level 1 still has to slow reads first (§7), so
    /// the only live lever left - the CPU hard cap - is lowered instead. Scanning/hashing is
    /// CPU-bound, so this throttles the read rate; upload limits stay untouched.
    /// </summary>
    public ThrottleSettings ForPlatform(ThrottleSettings settings, bool diskCapAvailable) =>
        diskCapAvailable || settings.Level != 1
            ? settings
            : settings with { CpuPercent = Math.Max(MinimumCpuPercent, Math.Round(settings.CpuPercent * 0.6, 1)) };

    private bool IsUnderPressure(PressureSample s) =>
        s.DiskLatencyMs > _options.DiskLatencyHighMs
        || s.SystemCpuPercent > _options.SystemCpuHighPercent
        || s.AvailableMemoryMb < _options.MinAvailableMemoryMb
        || (_budget.YieldToProtectedApps && s.ProtectedAppActive && Level < 1);

    private bool IsHealthy(PressureSample s) =>
        (s.DiskLatencyMs is null || s.DiskLatencyMs < _options.DiskLatencyLowMs)
        && (s.SystemCpuPercent is null || s.SystemCpuPercent < _options.SystemCpuLowPercent)
        && (s.AvailableMemoryMb is null || s.AvailableMemoryMb >= _options.MinAvailableMemoryMb)
        // Eco never relaxes below level 1 while the protected app (e.g. Tally) is in use.
        && !(_budget.YieldToProtectedApps && s.ProtectedAppActive && Level <= 1);
}
