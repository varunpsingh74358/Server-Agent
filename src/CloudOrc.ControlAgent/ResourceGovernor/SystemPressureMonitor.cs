using System.Diagnostics;
using System.Runtime.Versioning;
using CloudOrc.ControlAgent.Configuration;

namespace CloudOrc.ControlAgent.ResourceGovernor;

public interface IPressureMonitor : IDisposable
{
    PressureSample Sample();
}

/// <summary>
/// Reads whole-machine pressure: total CPU, physical disk latency, available memory, and
/// whether a protected user application (Tally by default) is running. Every counter is
/// optional - a machine with disabled performance counters still gets the hard caps, just no
/// adaptive tightening on that metric.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemPressureMonitor : IPressureMonitor
{
    private readonly PerformanceCounter? _cpu;
    private readonly PerformanceCounter? _diskLatency;
    private readonly PerformanceCounter? _availableMemory;
    private readonly IReadOnlyList<string> _protectedProcessNames;

    public SystemPressureMonitor(ResourceGovernorOptions options)
    {
        _protectedProcessNames = options.ProtectedProcessNames;
        _cpu = TryCreate("Processor", "% Processor Time", "_Total");
        _diskLatency = TryCreate("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");
        _availableMemory = TryCreate("Memory", "Available MBytes", null);

        // Rate counters return 0 on the first read; prime them.
        TryRead(_cpu);
        TryRead(_diskLatency);
    }

    public PressureSample Sample()
    {
        var diskSeconds = TryRead(_diskLatency);
        return new PressureSample(
            TryRead(_cpu),
            diskSeconds is null ? null : diskSeconds * 1000,
            TryRead(_availableMemory),
            IsProtectedAppRunning());
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        _diskLatency?.Dispose();
        _availableMemory?.Dispose();
    }

    private bool IsProtectedAppRunning()
    {
        foreach (var name in _protectedProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }

        return false;
    }

    private static PerformanceCounter? TryCreate(string category, string counter, string? instance)
    {
        try
        {
            return instance is null
                ? new PerformanceCounter(category, counter, readOnly: true)
                : new PerformanceCounter(category, counter, instance, readOnly: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static double? TryRead(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return null;
        }

        try
        {
            return counter.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
