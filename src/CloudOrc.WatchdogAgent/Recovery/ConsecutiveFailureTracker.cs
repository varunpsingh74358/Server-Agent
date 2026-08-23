namespace CloudOrc.WatchdogAgent.Recovery;

/// <summary>
/// Counts consecutive health-check failures. A single transient failure must never
/// trigger a restart - only a sustained run of failures reaching the configured
/// threshold should. Pure in-memory counter with no I/O, kept independent of the
/// monitor loop so it can be unit tested directly.
/// </summary>
public sealed class ConsecutiveFailureTracker
{
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Resets the counter to zero. Returns the value it held before resetting.</summary>
    public int RecordSuccess()
    {
        var previous = ConsecutiveFailures;
        ConsecutiveFailures = 0;
        return previous;
    }

    /// <summary>Increments the counter and returns the new value.</summary>
    public int RecordFailure() => ++ConsecutiveFailures;

    public bool HasReachedThreshold(int threshold) => ConsecutiveFailures >= threshold;
}
