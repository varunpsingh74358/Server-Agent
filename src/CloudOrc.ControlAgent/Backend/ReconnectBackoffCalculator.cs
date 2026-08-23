using CloudOrc.ControlAgent.Configuration;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Pure exponential-backoff calculation for reconnect delays - no I/O, no waiting,
/// trivially unit testable. <see cref="NextDelay"/> doubles the delay on every call
/// (representing one more consecutive failure) up to the configured maximum;
/// <see cref="Reset"/> should be called as soon as a connection is successfully
/// established, so the next failure starts backing off from the initial delay again.
///
/// Simplification (documented, not a bug): a connection that connects and then
/// immediately drops repeatedly will reset the backoff on every brief success, resulting
/// in more frequent reconnect attempts than an ideal "stable connection" reset policy
/// would allow. Acceptable for local testing; a hardened version could require a minimum
/// connected duration before resetting.
/// </summary>
public sealed class ReconnectBackoffCalculator(BackendConnectionOptions options)
{
    private int _consecutiveFailures;

    public TimeSpan NextDelay()
    {
        _consecutiveFailures++;
        var seconds = options.ReconnectInitialDelaySeconds * Math.Pow(2, _consecutiveFailures - 1);
        seconds = Math.Min(seconds, options.ReconnectMaximumDelaySeconds);
        var delay = TimeSpan.FromSeconds(seconds);

        // Additive jitter, off by default (ReconnectJitterMaxMilliseconds = 0) so every
        // existing exact-value test/behavior is unaffected unless an operator opts in.
        if (options.ReconnectJitterMaxMilliseconds > 0)
        {
            delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, options.ReconnectJitterMaxMilliseconds));
        }

        return delay;
    }

    public void Reset() => _consecutiveFailures = 0;
}
