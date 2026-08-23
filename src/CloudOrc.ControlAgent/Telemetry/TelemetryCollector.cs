using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Protocol;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Telemetry;

/// <summary>
/// Collects a single periodic telemetry snapshot. Telemetry is NOT the generic command
/// queue - none of this runs PowerShell. Every metric is collected defensively: a failure
/// collecting one metric (e.g. a disk that goes offline mid-read) never prevents the
/// others from being reported, and is logged at Debug rather than crashing the agent.
///
/// The CPU counter is a long-lived instance reused across calls (a fresh
/// <see cref="PerformanceCounter"/> reports 0/garbage on its very first read since it
/// needs two samples to compute a rate) - the first telemetry tick after startup may
/// therefore show an inaccurate CPU value; subsequent ticks are accurate.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class TelemetryCollector : IDisposable
{
    private readonly ILogger<TelemetryCollector> _logger;
    private readonly PerformanceCounter? _cpuCounter;

    public TelemetryCollector(ILogger<TelemetryCollector> logger)
    {
        _logger = logger;
        _cpuCounter = TryCreateCpuCounter();
    }

    public TelemetryMessage Collect(AgentIdentity identity)
    {
        return new TelemetryMessage
        {
            AgentId = identity.AgentId,
            ServerId = identity.ServerId,
            Machine = new TelemetryMachineInfo
            {
                MachineName = identity.MachineName,
                Os = SafeGet(() => RuntimeInformation.OSDescription, fallback: null)
            },
            Cpu = SafeGet(CollectCpu, fallback: null),
            Memory = SafeGet(CollectMemory, fallback: null),
            Disks = SafeGet(CollectDisks, fallback: (IReadOnlyList<TelemetryDiskInfo>)[]) ?? [],
            UptimeSeconds = Environment.TickCount64 / 1000
        };
    }

    private TelemetryCpuInfo? CollectCpu()
    {
        if (_cpuCounter is null)
        {
            return null;
        }

        return new TelemetryCpuInfo { UsagePercent = Math.Round(_cpuCounter.NextValue(), 1) };
    }

    private static TelemetryMemoryInfo? CollectMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        var total = (long)status.ullTotalPhys;
        var available = (long)status.ullAvailPhys;

        return new TelemetryMemoryInfo
        {
            TotalBytes = total,
            AvailableBytes = available,
            UsedBytes = total - available
        };
    }

    private static List<TelemetryDiskInfo> CollectDisks()
    {
        var disks = new List<TelemetryDiskInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            try
            {
                disks.Add(new TelemetryDiskInfo
                {
                    Name = drive.Name,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.TotalFreeSpace,
                    UsedBytes = drive.TotalSize - drive.TotalFreeSpace
                });
            }
            catch (IOException)
            {
                // A drive that became unready between IsReady and reading its sizes -
                // skip it rather than fail the whole telemetry snapshot.
            }
        }

        return disks;
    }

    private PerformanceCounter? TryCreateCpuCounter()
    {
        try
        {
            var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            counter.NextValue(); // Prime it; the first real reading needs a prior sample.
            return counter;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "CPU performance counter is unavailable on this machine; CPU usage will be omitted from telemetry.");
            return null;
        }
    }

    private T? SafeGet<T>(Func<T?> getter, T? fallback)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to collect a telemetry metric; using fallback value.");
            return fallback;
        }
    }

    public void Dispose() => _cpuCounter?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MemoryStatusEx()
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
