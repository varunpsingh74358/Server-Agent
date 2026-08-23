using CloudOrc.WatchdogAgent.Configuration;

namespace CloudOrc.WatchdogAgent.Recovery;

/// <summary>
/// Prevents an aggressive infinite restart loop. Two independent controls are combined:
/// a hard cap on how many restart attempts may occur within a rolling time window, and an
/// exponential backoff delay between attempts that grows with each consecutive recovery
/// failure. Both are pure, deterministic, and driven by an injected <see cref="TimeProvider"/>
/// so they are fully unit testable without real waiting.
/// </summary>
public sealed class RecoveryRateLimiter(WatchdogOptions options, TimeProvider timeProvider)
{
    private readonly Queue<DateTimeOffset> _recentAttempts = new();
    private int _consecutiveRecoveryFailures;
    private DateTimeOffset? _lastAttemptAt;

    public RecoveryDecision Evaluate()
    {
        var now = timeProvider.GetUtcNow();
        PruneOldAttempts(now);

        if (_recentAttempts.Count >= options.MaxRestartAttemptsPerWindow)
        {
            return RecoveryDecision.Deny(
                $"Rate limit reached: {_recentAttempts.Count} restart attempt(s) already made within the last {options.RestartRateLimitWindowMinutes} minute(s) (max {options.MaxRestartAttemptsPerWindow}).");
        }

        if (_lastAttemptAt is { } last)
        {
            var backoff = ComputeBackoff();
            var elapsed = now - last;
            if (elapsed < backoff)
            {
                var remaining = backoff - elapsed;
                return RecoveryDecision.Deny(
                    $"Backing off after {_consecutiveRecoveryFailures} consecutive failed recovery attempt(s): {remaining.TotalSeconds:F0}s remaining before the next attempt is allowed.");
            }
        }

        return RecoveryDecision.Allow();
    }

    /// <summary>Call immediately before actually attempting a restart.</summary>
    public void RecordAttempt()
    {
        var now = timeProvider.GetUtcNow();
        _recentAttempts.Enqueue(now);
        _lastAttemptAt = now;
        PruneOldAttempts(now);
    }

    /// <summary>Call after a restart attempt with whether the Control Agent became healthy again.</summary>
    public void RecordOutcome(bool succeeded)
    {
        _consecutiveRecoveryFailures = succeeded ? 0 : _consecutiveRecoveryFailures + 1;
    }

    public TimeSpan ComputeBackoff()
    {
        if (_consecutiveRecoveryFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = options.InitialBackoffSeconds * Math.Pow(2, _consecutiveRecoveryFailures - 1);
        seconds = Math.Min(seconds, options.MaxBackoffSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private void PruneOldAttempts(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(options.RestartRateLimitWindowMinutes);
        while (_recentAttempts.Count > 0 && now - _recentAttempts.Peek() > window)
        {
            _recentAttempts.Dequeue();
        }
    }
}
