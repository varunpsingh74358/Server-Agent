namespace CloudOrc.WatchdogAgent.Tests.Recovery;

/// <summary>
/// Minimal controllable <see cref="TimeProvider"/> for deterministically testing
/// <see cref="CloudOrc.WatchdogAgent.Recovery.RecoveryRateLimiter"/> without real waiting.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
