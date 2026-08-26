using System.Diagnostics;

namespace CloudOrc.WatchdogAgent.ControlAgentManagement;

/// <summary>
/// Samples one process's CPU and memory usage on repeated calls to <see cref="Sample"/>.
/// The process is re-resolved on every call via the delegate passed to the constructor -
/// use <see cref="ForProcessName"/> so a restarted Control Agent (new PID after a Watchdog
/// recovery) is picked up automatically, or <see cref="ForCurrentProcess"/> for the
/// Watchdog to report on itself.
///
/// CPU usage needs two timestamped samples to compute a rate, exactly like
/// <c>CloudOrc.ControlAgent.Telemetry.TelemetryCollector</c>'s own CPU counter - the first
/// sample after this instance is created, or the first sample after the tracked process
/// restarted under a new PID, reports <c>CpuPercent = null</c> rather than a misleading 0.
/// </summary>
public sealed class ProcessResourceSampler(Func<Process?> resolveProcess)
{
    private int? _previousProcessId;
    private DateTime _previousSampledAtUtc;
    private TimeSpan _previousCpuTime;

    public static ProcessResourceSampler ForProcessName(string processName) =>
        new(() =>
        {
            var candidates = Process.GetProcessesByName(processName);
            if (candidates.Length == 0)
            {
                return null;
            }

            // The Watchdog only ever tracks a single Control Agent instance, matching
            // its single-service monitoring model everywhere else - use the first match
            // and release the handles of any (unexpected) extras immediately.
            for (var i = 1; i < candidates.Length; i++)
            {
                candidates[i].Dispose();
            }

            return candidates[0];
        });

    public static ProcessResourceSampler ForCurrentProcess() => new(Process.GetCurrentProcess);

    /// <summary>Never throws - a process that has exited, or exits mid-read, is reported as not running rather than propagating an exception.</summary>
    public ProcessResourceSnapshot Sample()
    {
        try
        {
            using var process = resolveProcess();
            if (process is null)
            {
                _previousProcessId = null;
                return ProcessResourceSnapshot.NotRunning;
            }

            process.Refresh();

            var processId = process.Id;
            var cpuTime = process.TotalProcessorTime;
            var workingSetBytes = process.WorkingSet64;
            var privateMemoryBytes = process.PrivateMemorySize64;
            var now = DateTime.UtcNow;

            double? cpuPercent = null;
            if (_previousProcessId == processId)
            {
                var elapsed = now - _previousSampledAtUtc;
                if (elapsed > TimeSpan.Zero)
                {
                    var cpuDeltaMs = (cpuTime - _previousCpuTime).TotalMilliseconds;
                    var capacityMs = elapsed.TotalMilliseconds * Environment.ProcessorCount;
                    cpuPercent = Math.Clamp(Math.Round(cpuDeltaMs / capacityMs * 100, 1), 0, 100);
                }
            }

            _previousProcessId = processId;
            _previousSampledAtUtc = now;
            _previousCpuTime = cpuTime;

            return new ProcessResourceSnapshot
            {
                IsRunning = true,
                ProcessId = processId,
                CpuPercent = cpuPercent,
                WorkingSetBytes = workingSetBytes,
                PrivateMemoryBytes = privateMemoryBytes
            };
        }
        catch (InvalidOperationException)
        {
            // The process exited between being resolved and being read.
            _previousProcessId = null;
            return ProcessResourceSnapshot.NotRunning;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or the process exited at the OS level mid-read.
            _previousProcessId = null;
            return ProcessResourceSnapshot.NotRunning;
        }
    }
}
