using System.Diagnostics;
using System.Runtime.InteropServices;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.ResourceGovernor;

/// <summary>Latest self-measurement of Agent Core (the parent process).</summary>
public sealed record CoreBudgetSample(long WorkingSetBytes, long PrivateBytes, double? CpuPercent, bool OverBudget, DateTimeOffset At);

/// <summary>
/// The PARENT half of §7 "Enforce both parent Agent Core and child Kopia/Restic process budgets":
/// the worker tree is hard-capped by its Job Object; Agent Core itself must stay small and
/// responsive and must never be killed by a cap (it owns identity, heartbeat and updates, §3).
///
/// So Core is governed softly:
/// - measures its own working set, private bytes and CPU every minute;
/// - when idle (no backup job running) and above the idle target (§7 "Idle Core target ≤50 MB"),
///   compacts the managed heap and trims its working set - pages are only re-faulted on demand;
/// - logs and flags any breach of the hard private-memory / CPU budget so it shows up in
///   telemetry and the pilot evidence.
/// </summary>
public sealed class CoreBudgetMonitor(
    IOptions<BackupOptions> options,
    IServiceProvider services,
    ILogger<CoreBudgetMonitor> logger) : BackgroundService
{
    private readonly CoreBudgetOptions _options = options.Value.CoreBudget;
    private readonly Lock _sync = new();
    private CoreBudgetSample? _latest;
    private TimeSpan _lastCpu;
    private long _lastTicks;
    private bool _breachLogged;

    public CoreBudgetSample? Latest
    {
        get
        {
            lock (_sync)
            {
                return _latest;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            // Let startup (DI, JIT, first connection) settle before the first trim.
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.StartupDelaySeconds)), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                SampleAndTrim();
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _options.SampleIntervalSeconds)), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One measurement (+ trim when idle and above target). Public for tests.</summary>
    public CoreBudgetSample SampleAndTrim()
    {
        using var self = Process.GetCurrentProcess();
        var cpu = MeasureCpu(self);
        var idle = !IsBackupJobRunning();

        if (idle && self.WorkingSet64 > _options.IdleWorkingSetTargetMb * 1024L * 1024)
        {
            Trim(self);
            self.Refresh();
        }

        var overBudget = self.PrivateMemorySize64 > _options.MaxPrivateMb * 1024L * 1024
                         || (cpu is { } c && c > _options.MaxCpuPercent);
        if (overBudget && !_breachLogged)
        {
            _breachLogged = true;
            logger.LogWarning(
                "Agent Core is above its budget: private={PrivateMb:F0}MB (max {MaxMb}), cpu={Cpu:F1}% (max {MaxCpu}%).",
                self.PrivateMemorySize64 / 1048576.0, _options.MaxPrivateMb, cpu, _options.MaxCpuPercent);
        }
        else if (!overBudget)
        {
            _breachLogged = false;
        }

        var sample = new CoreBudgetSample(self.WorkingSet64, self.PrivateMemorySize64, cpu, overBudget, DateTimeOffset.UtcNow);
        lock (_sync)
        {
            _latest = sample;
        }

        return sample;
    }

    private bool IsBackupJobRunning() =>
        services.GetService(typeof(Backup.BackupJobCoordinator)) is Backup.BackupJobCoordinator coordinator && coordinator.RunningJobId is not null;

    private double? MeasureCpu(Process self)
    {
        var now = Stopwatch.GetTimestamp();
        var cpu = self.TotalProcessorTime;
        double? percent = null;
        if (_lastTicks != 0)
        {
            var wall = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (wall > 0)
            {
                percent = Math.Round((cpu - _lastCpu).TotalSeconds / (wall * Environment.ProcessorCount) * 100, 2);
            }
        }

        _lastCpu = cpu;
        _lastTicks = now;
        return percent;
    }

    private static void Trim(Process self)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        if (OperatingSystem.IsWindows())
        {
            // (-1, -1) = remove as many pages as possible from the working set; they come back on demand.
            SetProcessWorkingSetSize(self.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
}
