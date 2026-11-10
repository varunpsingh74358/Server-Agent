using System.Runtime.Versioning;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Enrollment;

/// <summary>Device proof material sent with any enrollment (§4 machine identity proof, §10 mTLS).</summary>
public sealed record DeviceProof(string InstallationId, string? DevicePublicKey, string? CsrPem);

/// <summary>
/// Shared by both enrollment paths (managed-VPS token and activation code): prepares the device
/// key / CSR before enrolling and persists the backend's answer afterwards (DPAPI, machine scope).
/// </summary>
[SupportedOSPlatform("windows")]
public static class EnrollmentFinalizer
{
    public static DeviceProof PrepareDeviceProof(string installationId, string machineId, ILogger logger)
    {
        try
        {
            var deviceKey = new DeviceKeyStore();
            return new DeviceProof(installationId, deviceKey.GetPublicKeySpki(), deviceKey.CreateCsrPem(machineId));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            // Enrollment still works without it (older backends); session renewal will not.
            logger.LogWarning(ex, "Could not create/open the device key; enrolling without device proof.");
            return new DeviceProof(installationId, null, null);
        }
    }

    public static EnrolledAgentState Persist(
        EnrollmentResponse response,
        string method,
        string? fallbackApiBaseUrl,
        EnrolledStateStore store,
        ILogger logger)
    {
        string? thumbprint = null;
        if (!string.IsNullOrWhiteSpace(response.ClientCertificatePem))
        {
            try
            {
                thumbprint = new DeviceKeyStore().InstallClientCertificate(response.ClientCertificatePem);
                logger.LogInformation("mTLS client certificate installed (thumbprint {Thumbprint}).", thumbprint);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "The issued client certificate could not be installed; continuing with bearer credential only.");
            }
        }

        var state = new EnrolledAgentState
        {
            AgentId = response.AgentId,
            ServerId = response.ServerId,
            BackendUrl = response.BackendUrl,
            Credential = response.Credential,
            EnrolledAtUtc = DateTimeOffset.UtcNow,
            ApiBaseUrl = response.ApiBaseUrl ?? fallbackApiBaseUrl,
            PortalUrl = response.PortalUrl,
            CredentialExpiresAtUtc = response.CredentialExpiresAt,
            ClientCertificateThumbprint = thumbprint,
            EnrollmentMethod = method,
            CommandSigningPublicKey = response.CommandSigningPublicKey
        };

        store.Save(state);
        return state;
    }
}
