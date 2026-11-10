using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Transport;

public enum RenewalOutcome
{
    Renewed,
    NotPossible,
    Rejected,
    Failed
}

/// <summary>
/// §12 POST /api/v1/agent/session/renew {endpoint_id, nonce, proof}: proves possession of the
/// non-exportable device key and receives a fresh session credential. Called on a 401 from the
/// gateway, ahead of a known credential expiry, or on the typed agent.credential.renew command.
/// Anti-replay: a fresh random nonce + timestamp per call (the backend must reject reuse and
/// stale timestamps). Rate-limited locally so a rejecting backend is not hammered.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionRenewalService(
    BackendApiClient api,
    AgentIdentity identity,
    AgentCredentialProvider credentials,
    DeviceKeyStore deviceKey,
    TimeProvider time,
    ILogger<SessionRenewalService> logger) : ISessionRenewer
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public bool IsAvailable => api.IsConfigured && deviceKey.Exists() && !string.IsNullOrEmpty(credentials.Current);

    public async Task<RenewalOutcome> RenewAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return RenewalOutcome.NotPossible;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (now - _lastAttempt < MinimumInterval)
            {
                return RenewalOutcome.NotPossible;
            }

            _lastAttempt = now;
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var request = new SessionRenewRequest
            {
                EndpointId = identity.AgentId,
                Nonce = nonce,
                Timestamp = now,
                Proof = deviceKey.Sign(SessionProof.BuildPayload(identity.AgentId, nonce, now))
            };

            var response = await api.PostJsonAsync(ControlPlaneRoutes.SessionRenew, request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess && response.Read<SessionRenewResponse>(BackendApiClient.SnakeCaseJson) is { Credential: { Length: > 0 } credential } body)
            {
                credentials.Replace(credential, body.ExpiresAt);
                logger.LogInformation("Session credential renewed (expires {ExpiresAt}).", body.ExpiresAt?.ToString("O") ?? "never");
                return RenewalOutcome.Renewed;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                logger.LogWarning("Session renewal was rejected ({Status}); re-activation may be required.", response.Describe());
                return RenewalOutcome.Rejected;
            }

            logger.LogWarning("Session renewal failed ({Status}); will retry later.", response.Describe());
            return RenewalOutcome.Failed;
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "The device key could not sign the renewal proof.");
            return RenewalOutcome.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
