using System.Net;

namespace CloudOrc.ControlAgent.Transport;

public enum AuthorizationHealth
{
    Ok,

    /// <summary>401s: credential expired/invalid - renewal or re-activation needed.</summary>
    Expired,

    /// <summary>403s: endpoint/credential revoked - never retried indefinitely (§8).</summary>
    Revoked
}

public sealed record ReconnectDecision(TimeSpan? Delay, bool StopReconnecting, bool AttemptSessionRenewal, AuthorizationHealth Authorization, string RetryStatus);

/// <summary>
/// Pure reconnect policy for the agent WebSocket (§8): "Use bounded exponential full jitter for
/// reconnects; respect valid Retry-After; distinguish 429 overload, 503 unavailable, 401
/// renewal, 403 revoked ... Never retry revoked authorization indefinitely."
/// Returns null Delay when the caller's normal exponential full-jitter backoff applies.
/// </summary>
public sealed class ConnectionRetryPolicy
{
    public const int UnauthorizedBeforeExpired = 5;
    public const int ForbiddenBeforeStop = 3;
    public static readonly TimeSpan ExpiredRetryDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RevokedRetryDelay = TimeSpan.FromHours(1);

    private int _consecutiveUnauthorized;
    private int _consecutiveForbidden;

    public AuthorizationHealth Authorization { get; private set; } = AuthorizationHealth.Ok;

    public void OnConnected()
    {
        _consecutiveUnauthorized = 0;
        _consecutiveForbidden = 0;
        Authorization = AuthorizationHealth.Ok;
    }

    public ReconnectDecision OnFailure(HttpStatusCode? status, TimeSpan? retryAfter, bool renewalAvailable)
    {
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                _consecutiveForbidden = 0;
                _consecutiveUnauthorized++;
                if (_consecutiveUnauthorized == 1 && renewalAvailable)
                {
                    return new ReconnectDecision(TimeSpan.Zero, false, true, Authorization, "Renewing session credential");
                }

                if (_consecutiveUnauthorized >= UnauthorizedBeforeExpired)
                {
                    Authorization = AuthorizationHealth.Expired;
                    return new ReconnectDecision(ExpiredRetryDelay, false, renewalAvailable, Authorization,
                        "Authorization expired - reconnect this device from the CloudOrc portal");
                }

                return new ReconnectDecision(null, false, false, Authorization, "Credential rejected - retrying");

            case HttpStatusCode.Forbidden:
                _consecutiveUnauthorized = 0;
                _consecutiveForbidden++;
                Authorization = AuthorizationHealth.Revoked;
                return _consecutiveForbidden >= ForbiddenBeforeStop
                    ? new ReconnectDecision(null, true, false, Authorization, "Device access was revoked - re-activation required")
                    : new ReconnectDecision(RevokedRetryDelay, false, false, Authorization, "Device access was revoked");

            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.ServiceUnavailable:
                return new ReconnectDecision(retryAfter, false, false, Authorization,
                    status == HttpStatusCode.TooManyRequests ? "CloudOrc is busy - waiting as instructed" : "CloudOrc is temporarily unavailable");

            default:
                return new ReconnectDecision(null, false, false, Authorization, "Connection failed - retrying");
        }
    }
}
