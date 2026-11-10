using System.Diagnostics;
using System.Runtime.Versioning;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Identity;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.LocalStatus;

/// <summary>
/// Activation from the optional tray (§4 "User installs once, enters code ... and sees only
/// activation/connection status"). Runs inside the service (LocalSystem) so the credential is
/// written machine-scoped; the tray never sees it. Allowed only while the device is NOT
/// activated, or its authorization has expired/been revoked (§4 "Reconnect only if
/// authorization expired") - a working device cannot be re-pointed from the desktop.
/// After success the service restarts itself into enrolled mode.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActivationService(
    EnrolledStateStore store,
    BackendLinkState link,
    BackupPaths paths,
    IOptions<ActivationOptions> activationOptions,
    IOptions<ControlAgentOptions> controlOptions,
    IHostApplicationLifetime lifetime,
    ILogger<ActivationService> logger)
{
    private readonly Lock _sync = new();
    private CancellationTokenSource? _inFlight;

    public bool InProgress
    {
        get
        {
            lock (_sync)
            {
                return _inFlight is not null;
            }
        }
    }

    public bool RestartPending { get; private set; }

    public string? LastError { get; private set; }

    public bool CanActivate()
    {
        var enrolled = store.TryLoad();
        return enrolled is null || link.Authorization is not AuthorizationHealth.Ok || link.IsStopped;
    }

    public async Task<(bool Ok, string? Error)> ActivateAsync(string code, CancellationToken cancellationToken)
    {
        if (!CanActivate())
        {
            return (false, "This device is already activated and connected.");
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_inFlight is not null)
            {
                return (false, "Activation is already in progress.");
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
            _inFlight = cts;
        }

        try
        {
            var enrolled = store.TryLoad();
            var options = activationOptions.Value;
            var apiBaseText = enrolled?.ApiBaseUrl ?? options.ApiBaseUrl;
            if (!EnrollmentCommandLine.TryParseApiBase(apiBaseText, options.DevelopmentAllowInsecureHttp, out var apiBase))
            {
                return Fail("This installation does not know the CloudOrc address. Reinstall from the CloudOrc portal.");
            }

            var data = controlOptions.Value.DataDirectory;
            var machineId = MachineIdResolver.Resolve(data, logger);
            var installationId = BackupPaths.GetOrCreateInstallationId(paths.InstallationIdFile);
            var proof = EnrollmentFinalizer.PrepareDeviceProof(installationId, machineId, logger);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var outcome = await new ActivationCodeEnrollmentClient(http).EnrollAsync(
                apiBase!,
                code,
                new EndpointBinding
                {
                    MachineId = machineId,
                    MachineName = Environment.MachineName,
                    InstallationId = installationId,
                    OsVersion = Environment.OSVersion.VersionString,
                    DevicePublicKey = proof.DevicePublicKey,
                    CsrPem = proof.CsrPem
                },
                AgentVersionInfo.GetVersion(typeof(ActivationService).Assembly),
                cts.Token).ConfigureAwait(false);

            if (!outcome.IsSuccess)
            {
                return Fail(outcome.Error ?? "Activation failed.");
            }

            EnrollmentFinalizer.Persist(outcome.Response!, "activation-code", apiBase!.ToString(), store, logger);
            logger.LogInformation("Device activated from the local status app (agentId={AgentId}). Restarting into connected mode.", outcome.Response!.AgentId);
            LastError = null;
            RestartPending = true;
            ScheduleServiceRestart();
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return Fail("Activation was cancelled or timed out.");
        }
        finally
        {
            lock (_sync)
            {
                _inFlight?.Dispose();
                _inFlight = null;
            }
        }
    }

    public void CancelActivation()
    {
        lock (_sync)
        {
            _inFlight?.Cancel();
        }
    }

    private (bool, string?) Fail(string error)
    {
        LastError = error;
        logger.LogWarning("Local activation failed: {Error}", error);
        return (false, error);
    }

    /// <summary>
    /// Restarts the service so it starts again in enrolled mode. Uses this agent's own
    /// "restart-service" CLI mode in a detached process (it outlives our stop); when running as a
    /// console app (development) the host simply stops and must be started again by hand.
    /// </summary>
    private void ScheduleServiceRestart()
    {
        var exe = Environment.ProcessPath;
        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() && exe is not null)
        {
            try
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("restart-service");
                Process.Start(start)?.Dispose();
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger.LogError(ex, "Could not launch the restart helper; stopping so the service manager restarts the agent.");
            }
        }

        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => lifetime.StopApplication(), TaskScheduler.Default);
    }
}
