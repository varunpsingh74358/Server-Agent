using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.LocalStatus;

/// <summary>
/// Local status IPC for the optional tray (§4: "optional tray is a separate user-session process
/// using authenticated local IPC"). A named pipe that only local authenticated users can open
/// (no network access: remote clients are denied). One JSON request line → one JSON response
/// line per connection. Exposes only <see cref="LocalStatusResponse"/>; never a secret.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentStatusPipeServer(
    EnrolledStateStore store,
    BackendLinkState link,
    ActivationService activation,
    BackupPaths paths,
    IServiceProvider services,
    IOptions<BackendConnectionOptions> backendOptions,
    IOptions<ActivationOptions> activationOptions,
    TimeProvider time,
    ILogger<AgentStatusPipeServer> logger) : BackgroundService
{
    private const int MaxRequestBytes = 4096;
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private readonly string _installationId = BackupPaths.GetOrCreateInstallationId(paths.InstallationIdFile);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Local status pipe listening on '{Pipe}'.", LocalStatusProtocol.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    LocalStatusProtocol.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    CreateSecurity());

                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                var connection = server;
                server = null;
                _ = Task.Run(() => ServeAsync(connection, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Local status pipe error; recreating.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));

                var line = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
                var request = line is null ? null : TryParse(line);
                var response = await HandleAsync(request, timeout.Token).ConfigureAwait(false);

                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, ProtocolJson.Options) + "\n");
                await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "Local status client disconnected.");
            }
        }
    }

    private async Task<LocalStatusResponse> HandleAsync(LocalStatusRequest? request, CancellationToken cancellationToken)
    {
        switch (request?.Action)
        {
            case LocalStatusProtocol.ActionStatus:
                return BuildStatus();

            case LocalStatusProtocol.ActionActivate:
                var (ok, error) = await activation.ActivateAsync(request.Code ?? string.Empty, cancellationToken).ConfigureAwait(false);
                return BuildStatus(ok, error);

            case LocalStatusProtocol.ActionCancelActivation:
                activation.CancelActivation();
                return BuildStatus();

            case LocalStatusProtocol.ActionRetry:
                link.RequestReconnect();
                return BuildStatus();

            default:
                return BuildStatus(false, "Unknown request.");
        }
    }

    public LocalStatusResponse BuildStatus(bool ok = true, string? error = null)
    {
        var jobs = services.GetService(typeof(BackupJobStore)) as BackupJobStore;
        var policies = services.GetService(typeof(BackupPolicyStore)) as BackupPolicyStore;

        IReadOnlyList<BackupJobRecord> recent = [];
        var policyCount = 0;
        DateTimeOffset? lastSuccess = null;
        DateTimeOffset? lastVerified = null;
        try
        {
            recent = jobs?.GetRecent(50) ?? [];
            var all = policies?.GetAll() ?? [];
            policyCount = all.Count;
            foreach (var policy in all)
            {
                var s = policies!.GetScheduleState(policy.PolicyId);
                if (s.LastSuccessAt > lastSuccess || lastSuccess is null)
                {
                    lastSuccess = s.LastSuccessAt;
                }

                if (s.LastVerifiedAt > lastVerified || lastVerified is null)
                {
                    lastVerified = s.LastVerifiedAt;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read backup state for the local status.");
        }

        return LocalStatusBuilder.Build(new LocalStatusInputs(
            store.TryLoad(),
            backendOptions.Value.Enabled,
            link.IsConnected,
            link.IsStopped,
            link.Authorization,
            link.RetryStatus,
            link.LastContactAt,
            _startedAt,
            time.GetUtcNow(),
            activation.InProgress,
            activation.RestartPending,
            activation.LastError,
            _installationId,
            AgentVersionInfo.GetVersion(typeof(AgentStatusPipeServer).Assembly),
            activationOptions.Value.PortalUrl,
            policyCount,
            lastSuccess,
            lastVerified,
            recent.FirstOrDefault(j => BackupJobStates.IsActive(j.State)),
            recent.Where(j => j.Kind == BackupJobKinds.Backup && j.State == BackupJobStates.Failed).OrderByDescending(j => j.EndedAt).FirstOrDefault()),
            ok, error);
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Local only: deny anything arriving over the network.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private static LocalStatusRequest? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<LocalStatusRequest>(line, ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer[total] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer, 0, total);
            }

            total += read;
        }

        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }
}
