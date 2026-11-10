using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.Enrollment;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Transport;

public enum RotationOutcome
{
    NotNeeded,
    NoCertificate,
    Rotated,
    Rejected,
    Failed
}

/// <summary>
/// §4 "mTLS credential issuance and rotation", §10 "Device mTLS identity". Rotates the client
/// certificate well before it expires: when two thirds of its lifetime have passed or fewer than
/// 14 days remain. The request carries a fresh CSR from the same non-exportable device key plus a
/// device-key proof (anti-replay nonce + timestamp), so a stolen certificate file alone cannot be
/// used to obtain a new one. On success the new certificate is installed, the persisted thumbprint
/// is switched, and the old certificate is removed from LocalMachine\My. New WebSocket and HTTPS
/// connections pick it up on their next handshake.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CertificateRotationService(
    BackendApiClient api,
    AgentIdentity identity,
    EnrolledStateStore store,
    DeviceKeyStore deviceKey,
    TimeProvider time,
    ILogger<CertificateRotationService> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan MinimumRemaining = TimeSpan.FromDays(14);

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RotateIfDueAsync(force: false, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Client certificate rotation check failed.");
                }

                await Task.Delay(CheckInterval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>True when a certificate valid from <paramref name="notBefore"/> to <paramref name="notAfter"/> should be renewed now.</summary>
    public static bool IsDue(DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now)
    {
        var lifetime = notAfter - notBefore;
        return now >= notBefore + (lifetime * 2 / 3) || notAfter - now <= MinimumRemaining;
    }

    public async Task<RotationOutcome> RotateIfDueAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = store.TryLoad();
            using var current = DeviceKeyStore.FindClientCertificate(state?.ClientCertificateThumbprint);
            if (state is null || current is null)
            {
                return RotationOutcome.NoCertificate;
            }

            if (!force && !IsDue(new DateTimeOffset(current.NotBefore.ToUniversalTime()), new DateTimeOffset(current.NotAfter.ToUniversalTime()), time.GetUtcNow()))
            {
                return RotationOutcome.NotNeeded;
            }

            var csr = deviceKey.CreateCsrPem(identity.MachineId);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var now = time.GetUtcNow();
            var csrHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csr)));
            var request = new CertificateRenewRequest
            {
                EndpointId = identity.AgentId,
                CsrPem = csr,
                CurrentThumbprint = current.Thumbprint,
                Nonce = nonce,
                Timestamp = now,
                Proof = deviceKey.Sign(CertificateProof.BuildPayload(identity.AgentId, nonce, now, csrHash))
            };

            var response = await api.PostJsonAsync(ControlPlaneRoutes.CertificateRenew, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                logger.LogWarning("Client certificate renewal refused/failed ({Status}).", response.Describe());
                return response.IsTransient ? RotationOutcome.Failed : RotationOutcome.Rejected;
            }

            var pem = response.Read<CertificateRenewResponse>(BackendApiClient.SnakeCaseJson)?.ClientCertificatePem;
            if (string.IsNullOrWhiteSpace(pem))
            {
                return RotationOutcome.Failed;
            }

            var newThumbprint = deviceKey.InstallClientCertificate(pem);
            store.Save(state.With(clientCertificateThumbprint: newThumbprint));
            RemoveFromStore(current.Thumbprint, newThumbprint);
            logger.LogInformation("Client certificate rotated ({Old} -> {New}).", current.Thumbprint, newThumbprint);
            return RotationOutcome.Rotated;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Client certificate rotation failed.");
            return RotationOutcome.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void RemoveFromStore(string oldThumbprint, string newThumbprint)
    {
        if (string.Equals(oldThumbprint, newThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var certStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        certStore.Open(OpenFlags.ReadWrite);
        foreach (var cert in certStore.Certificates.Find(X509FindType.FindByThumbprint, oldThumbprint, validOnly: false))
        {
            certStore.Remove(cert);
        }
    }
}

/// <summary>agent.certificate.renew - rotate the mTLS client certificate now.</summary>
[SupportedOSPlatform("windows")]
public sealed class CertificateRenewHandler(CertificateRotationService rotation) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.AgentCertificateRenew;

    public async Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var outcome = await rotation.RotateIfDueAsync(force: true, cancellationToken).ConfigureAwait(false);
        return outcome == RotationOutcome.Rotated
            ? new TypedCommandAcceptance { Accepted = true, Message = "Client certificate rotated." }
            : TypedCommandDispatcher.Rejected("CERTIFICATE_ROTATION_" + outcome.ToString().ToUpperInvariant(), $"Certificate rotation: {outcome}.");
    }
}
