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

        // CO-DEV-BKP-001 §8: "Use bounded exponential full jitter for reconnects". Full jitter
        // picks uniformly in [1s, cap] so a fleet that lost the backend at the same instant does
        // not come back in synchronized waves. Opt-in (ReconnectFullJitter) so the exact-value
        // behaviour above stays the default for existing deterministic tests/local runs.
        if (options.ReconnectFullJitter)
        {
            var floor = Math.Min(1.0, seconds);
            return TimeSpan.FromSeconds(floor + (Random.Shared.NextDouble() * (seconds - floor)));
        }

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
