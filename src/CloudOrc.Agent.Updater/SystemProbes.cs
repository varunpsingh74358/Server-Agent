using System.Diagnostics;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudOrc.Agent.Updater;

public sealed class WindowsServiceControl(IUpdateLog log) : IServiceControl
{
    public bool IsRunning(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public bool Stop(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status is ServiceControllerStatus.Stopped)
            {
                return true;
            }

            if (sc.Status is not ServiceControllerStatus.StopPending)
            {
                sc.Stop();
            }

            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            log.Warn($"Timed out waiting for service {serviceName} to stop.");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Warn($"Could not stop service {serviceName}: {ex.Message}");
            return false;
        }
    }

    public bool Start(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status is ServiceControllerStatus.Running)
            {
                return true;
            }

            if (sc.Status is not ServiceControllerStatus.StartPending)
            {
                sc.Start();
            }

            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            log.Warn($"Timed out waiting for service {serviceName} to start.");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Warn($"Could not start service {serviceName}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Reads one JSON snapshot from the Control Agent's health pipe and returns its "status".</summary>
public sealed class PipeHealthProbe(string pipeName = PipeHealthProbe.DefaultPipeName) : IHealthProbe
{
    public const string DefaultPipeName = "CloudOrc.ControlAgent.Health";

    public async Task<string?> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(client);
            var json = await reader.ReadToEndAsync(connectCts.Token).ConfigureAwait(false);
            return ParseStatus(json);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? ParseStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Runs <c>CloudOrc.ControlAgent.exe --version</c> and parses the "Version: X" line.</summary>
public sealed partial class ControlAgentVersionProbe(string controlAgentExe) : IVersionProbe
{
    public async Task<string?> GetInstalledVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(controlAgentExe))
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(controlAgentExe, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(controlAgentExe)!
                }
            };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            _ = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }

            return ParseVersion(await stdoutTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    internal static string? ParseVersion(string output)
    {
        var match = VersionLine().Match(output);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    [GeneratedRegex(@"^\s*Version:\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex VersionLine();
}

/// <summary>Runs the Inno Setup installer silently, killing it (and its children) on timeout.</summary>
public sealed class InnoInstallerRunner(IUpdateLog log) : IInstallerRunner
{
    public async Task<int?> RunAsync(string installerPath, string installerLogPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add($"/LOG={installerLogPath}");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Error($"Could not start installer: {ex.Message}");
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            log.Error($"Installer did not finish within {timeout.TotalMinutes:F0} minutes; killing it.");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // best effort
            }

            return null;
        }
    }
}
