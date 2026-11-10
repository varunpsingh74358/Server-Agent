using System.Diagnostics;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.ResourceGovernor;

/// <summary>
/// Governs ONE job: owns its Job Object, samples system pressure every few seconds, moves the
/// adaptive throttle, re-applies OS caps live, and keeps the latest §7 metrics for events.
/// Disposing it closes the Job Object (kill-on-close), so nothing can outlive the job.
/// </summary>
public sealed class GovernorSession : IAsyncDisposable
{
    private readonly IJobObject _job;
    private readonly IPressureMonitor _pressure;
    private readonly AdaptiveThrottleController _controller;
    private readonly ResourceGovernorOptions _options;
    private readonly IReadOnlyCollection<string> _volumeRoots;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _sync = new();
    private Task? _loop;
    private ResourceSample? _latest;
    private long _lastCpuTime;
    private long _lastCpuTicks;
    private bool _diskCapWarned;
    private bool _diskCapAvailable = true;

    public GovernorSession(
        ResourceBudget budget,
        IJobObject job,
        IPressureMonitor pressure,
        ResourceGovernorOptions options,
        IReadOnlyCollection<string> volumeRoots,
        ILogger logger)
    {
        Budget = budget;
        _job = job;
        _pressure = pressure;
        _options = options;
        _volumeRoots = volumeRoots;
        _logger = logger;
        _controller = new AdaptiveThrottleController(budget, options);
    }

    public ResourceBudget Budget { get; }

    public bool IsMaintenance { get; init; }

    /// <summary>Whether per-volume disk bandwidth caps actually took effect on this OS.</summary>
    public bool DiskCapAvailable => _diskCapAvailable;

    /// <summary>Latest upload rate reported by the worker (bytes/s), folded into samples.</summary>
    public double? UploadBytesPerSecond { get; set; }

    public long? ScannerDurationMs { get; set; }

    public long? CacheSizeBytes { get; set; }

    /// <summary>Applies the static caps and puts <paramref name="worker"/> under them, then starts adapting.</summary>
    public void Start(Process worker)
    {
        _job.Configure(Budget);
        ApplyDiskCap(Budget.MaxDiskBytesPerSecond);
        _job.Assign(worker);
        _lastCpuTicks = Stopwatch.GetTimestamp();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation(
            "Resource governor: profile={Profile} cpu<={Cpu}% mem<={MemMb}MB disk<={Disk} upload<={Upload} parallel={Parallel} priority={Priority}.",
            Budget.Profile, Budget.MaxCpuPercent, Budget.MaxMemoryBytes / (1024 * 1024),
            Budget.MaxDiskBytesPerSecond is { } d ? $"{d / (1024 * 1024)}MB/s" : "uncapped",
            Budget.MaxUploadBytesPerSecond is { } u ? $"{u * 8 / 1_000_000}Mbit/s" : "lease",
            Budget.Parallelism, Budget.Priority);
    }

    public ResourceSample? LatestSample
    {
        get
        {
            lock (_sync)
            {
                return _latest;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _job.Dispose();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.SampleIntervalSeconds));
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(interval, token).ConfigureAwait(false);
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // A sampling failure must never take the job down - the static caps still hold.
                _logger.LogDebug(ex, "Resource governor sample failed.");
            }
        }
    }

    internal void Tick()
    {
        var pressure = _pressure.Sample();
        var (raw, changed) = _controller.Evaluate(pressure);
        if (changed)
        {
            ApplyDiskCap(raw.DiskBytesPerSecond);
        }

        var settings = _controller.ForPlatform(raw, _diskCapAvailable);
        if (changed)
        {
            _job.SetCpuRate(settings.CpuPercent);
            _logger.LogInformation(
                "Resource governor throttle level {Level} (cpu<={Cpu}%, disk<={Disk}); pressure cpu={SysCpu:F0}% disk={DiskMs:F1}ms availMem={Mem:F0}MB protectedApp={App}.",
                settings.Level, settings.CpuPercent,
                settings.DiskBytesPerSecond is { } d ? $"{d / (1024 * 1024)}MB/s" : "uncapped",
                pressure.SystemCpuPercent, pressure.DiskLatencyMs, pressure.AvailableMemoryMb, pressure.ProtectedAppActive);
        }

        var usage = _job.QueryUsage();
        var nowTicks = Stopwatch.GetTimestamp();
        var wall100Ns = (nowTicks - _lastCpuTicks) * 10_000_000.0 / Stopwatch.Frequency;
        double? workerCpu = wall100Ns > 0 && _lastCpuTime > 0
            ? Math.Round((usage.TotalCpuTime100Ns - _lastCpuTime) / (wall100Ns * Environment.ProcessorCount) * 100, 1)
            : null;
        _lastCpuTime = usage.TotalCpuTime100Ns;
        _lastCpuTicks = nowTicks;

        using var self = Process.GetCurrentProcess();
        var sample = new ResourceSample
        {
            Profile = Budget.Profile,
            ThrottleLevel = settings.Level,
            WorkerWorkingSetBytes = usage.WorkingSetBytes,
            WorkerPeakMemoryBytes = usage.PeakJobMemoryBytes,
            WorkerCpuPercent = workerCpu,
            SystemCpuPercent = pressure.SystemCpuPercent is { } c ? Math.Round(c, 1) : null,
            DiskLatencyMs = pressure.DiskLatencyMs is { } l ? Math.Round(l, 2) : null,
            AvailableMemoryMb = pressure.AvailableMemoryMb,
            UploadBytesPerSecond = UploadBytesPerSecond,
            ScannerDurationMs = ScannerDurationMs,
            CacheSizeBytes = CacheSizeBytes,
            ProtectedAppActive = pressure.ProtectedAppActive,
            AgentCoreWorkingSetBytes = self.WorkingSet64,
            Maintenance = IsMaintenance
        };

        lock (_sync)
        {
            _latest = sample;
        }
    }

    private void ApplyDiskCap(long? bytesPerSecond)
    {
        if (_volumeRoots.Count == 0)
        {
            return;
        }

        var applied = _job.SetDiskBandwidth(_volumeRoots, bytesPerSecond);
        if (bytesPerSecond is > 0)
        {
            _diskCapAvailable = applied;
        }

        if (!applied && bytesPerSecond is > 0 && !_diskCapWarned)
        {
            _diskCapWarned = true;
            _logger.LogWarning(
                "Per-volume disk bandwidth control is unavailable on this system; the governor slows reads through the CPU cap instead (engine parallelism still bounds disk use).");
        }
    }
}

/// <summary>Creates governor sessions (seam for tests and non-Windows builds).</summary>
public interface IResourceGovernor
{
    ResourceBudget ResolveBudget(string? profile, AdmissionLease? lease);

    GovernorSession CreateSession(ResourceBudget budget, IReadOnlyCollection<string> volumeRoots, bool maintenance);
}

public sealed class WindowsResourceGovernor(
    Microsoft.Extensions.Options.IOptions<BackupOptions> options,
    ILogger<WindowsResourceGovernor> logger) : IResourceGovernor
{
    private readonly BackupOptions _options = options.Value;

    public ResourceBudget ResolveBudget(string? profile, AdmissionLease? lease)
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var budget = ResourceProfiles.Resolve(profile, Environment.ProcessorCount, memory);
        return ResourceProfiles.ApplyLease(budget, lease);
    }

    public GovernorSession CreateSession(ResourceBudget budget, IReadOnlyCollection<string> volumeRoots, bool maintenance)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The resource governor requires Windows Job Objects.");
        }

        return new GovernorSession(
            budget,
            new WindowsJobObject(),
            new SystemPressureMonitor(_options.Governor),
            _options.Governor,
            volumeRoots,
            logger)
        {
            IsMaintenance = maintenance
        };
    }
}
