using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Transport;

/// <summary>
/// Renews a short-lived session credential BEFORE it expires (§10 "short-lived session
/// credentials"), so the agent normally never hits a 401 at all. No-op when the backend issues
/// credentials without an expiry.
/// </summary>
public sealed class CredentialRenewalScheduler(
    AgentCredentialProvider credentials,
    ISessionRenewer renewer,
    TimeProvider time,
    ILogger<CredentialRenewalScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan RenewLead = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ShouldRenew(credentials.ExpiresAt, time.GetUtcNow()) && renewer.IsAvailable)
                {
                    var outcome = await renewer.RenewAsync(stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Scheduled credential renewal: {Outcome}.", outcome);
                }

                await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Credential renewal check failed.");
                await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public static bool ShouldRenew(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is { } exp && now >= exp - RenewLead;
}
