using System.ServiceProcess;
using CloudOrc.WatchdogAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudOrc.WatchdogAgent.ControlAgentManagement;

/// <summary>
/// Thin wrapper over <see cref="ServiceController"/> for the CloudOrcControlAgent
/// Windows Service. This talks to the Windows Service Control Manager only - it never
/// runs arbitrary PowerShell to manage the service. Not unit tested directly: querying
/// and restarting a real Windows Service is inherently an integration concern that
/// requires the service to actually be installed (see docs/TESTING.md).
/// </summary>
public sealed class ControlAgentServiceManager(WatchdogOptions options, ILogger<ControlAgentServiceManager> logger)
{
    /// <summary>Returns null when the service is not installed at all.</summary>
    public ServiceControllerStatus? TryGetStatus()
    {
        try
        {
            using var controller = new ServiceController(options.ControlAgentServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning(ex, "Transient error querying status of service {ServiceName}.", options.ControlAgentServiceName);
            return null;
        }
    }

    /// <summary>
    /// Restarts the Control Agent Windows Service. Returns false (and logs why) when the
    /// service is not installed - in console/dev mode there is no Windows Service for the
    /// Watchdog to restart, which is an expected and documented limitation.
    /// </summary>
    public bool TryRestart()
    {
        try
        {
            using var controller = new ServiceController(options.ControlAgentServiceName);
            controller.Refresh();

            if (controller.Status is not ServiceControllerStatus.Stopped)
            {
                logger.LogInformation("Stopping service {ServiceName} before restart.", options.ControlAgentServiceName);
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            logger.LogInformation("Starting service {ServiceName}.", options.ControlAgentServiceName);
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return true;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                ex,
                "Cannot restart service {ServiceName} - it is not installed. Restart is only possible once both agents " +
                "are installed as Windows Services; see docs/WINDOWS_SERVICE_INSTALLATION.md.",
                options.ControlAgentServiceName);
            return false;
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            logger.LogError(ex, "Timed out waiting for service {ServiceName} to reach the expected state during restart.", options.ControlAgentServiceName);
            return false;
        }
    }
}
