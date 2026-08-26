using CloudOrc.WatchdogAgent.Configuration;

namespace CloudOrc.WatchdogAgent.ControlAgentManagement;

/// <summary>
/// Bundles the two resource samplers the Watchdog's monitoring cycle reports every tick:
/// the Control Agent it watches (re-resolved by process name every sample, so a restart
/// under a new PID is picked up automatically), and the Watchdog itself - a Windows
/// Service that runs indefinitely is worth being able to see the resource usage of too,
/// not just the thing it watches.
/// </summary>
public sealed class WatchdogResourceMonitors
{
    public ProcessResourceSampler ControlAgent { get; }

    public ProcessResourceSampler Self { get; }

    public WatchdogResourceMonitors(WatchdogOptions options)
    {
        ControlAgent = ProcessResourceSampler.ForProcessName(options.ControlAgentProcessName);
        Self = ProcessResourceSampler.ForCurrentProcess();
    }
}
