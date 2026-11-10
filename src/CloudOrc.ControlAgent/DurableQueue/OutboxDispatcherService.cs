using System.Net;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.DurableQueue;

/// <summary>
/// Delivers the durable outbox to the HTTPS control plane (§11 at-least-once; §12 job events).
/// Strictly in sequence order (head-of-line) so the backend sees each job's events in order.
///
/// Per row: 2xx or 409 → delivered (deleted; 409 = backend inbox already has that event_id).
/// 401/408/429/5xx/network → retry later with exponential full jitter, honouring Retry-After
/// (§8 "no infinite retry storm"). 400/403/404/413/422 → poison: parked as dead (kept 7 days for
/// diagnostics) so one bad message cannot block every later event.
/// </summary>
public sealed class OutboxDispatcherService(
    OutboxStore outbox,
    BackendApiClient api,
    BackendLinkState link,
    TimeProvider time,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    public void Wake() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        link.ReconnectRequested += Wake;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox delivery pass failed.");
                wait = IdlePoll;
            }

            try
            {
                await _wake.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Sends what is ready; returns how long to wait before the next pass.</summary>
    public async Task<TimeSpan> DrainAsync(CancellationToken cancellationToken)
    {
        if (!api.IsConfigured)
        {
            return IdlePoll;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = outbox.PeekHead(25);
            if (batch.Count == 0)
            {
                return IdlePoll;
            }

            foreach (var item in batch)
            {
                var now = time.GetUtcNow();
                if (item.NextAttemptAt > now)
                {
                    var until = item.NextAttemptAt - now;
                    return until < IdlePoll ? until : IdlePoll;
                }

                var response = await SendAsync(item, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccess || response.IsDuplicate)
                {
                    outbox.MarkDelivered(item.Sequence);
                    TryDeleteFile(item.FilePath);
                    continue;
                }

                if (response.IsTransient || response.StatusCode is null)
                {
                    var delay = response.RetryAfter ?? Backoff(item.Attempts + 1);
                    outbox.MarkFailed(item.Sequence, response.Describe(), now + delay, dead: false);
                    if (item.Attempts is 0 or 5 or 20)
                    {
                        logger.LogWarning("Outbox delivery of {Kind} {EventId} failed ({Status}); retrying in {Delay:F0}s.",
                            item.Kind, item.EventId, response.Describe(), delay.TotalSeconds);
                    }

                    return delay;
                }

                logger.LogError("Outbox {Kind} {EventId} was rejected permanently ({Status}); parked as dead.",
                    item.Kind, item.EventId, response.Describe());
                outbox.MarkFailed(item.Sequence, $"{response.Describe()}: {Trim(response.Body)}", now, dead: true);
            }
        }

        return IdlePoll;
    }

    private Task<ApiResponse> SendAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        if (item.FilePath is not null)
        {
            if (!File.Exists(item.FilePath))
            {
                return Task.FromResult(new ApiResponse(HttpStatusCode.Gone, "Local file missing.", null, null));
            }

            return api.PostFileAsync(item.Route, item.FilePath, item.ContentType ?? "application/octet-stream",
                gzipEncoded: item.FilePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase), cancellationToken);
        }

        return api.PostJsonStringAsync(item.Route, item.Payload ?? "{}", cancellationToken);
    }

    private static TimeSpan Backoff(int attempt)
    {
        var cap = Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Min(attempt, 12)));
        return TimeSpan.FromSeconds(1 + (Random.Shared.NextDouble() * (cap - 1)));
    }

    private void TryDeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete delivered file {Path}.", path);
        }
    }

    private static string Trim(string? body) => body is null ? "" : body.Length > 200 ? body[..200] : body;
}
