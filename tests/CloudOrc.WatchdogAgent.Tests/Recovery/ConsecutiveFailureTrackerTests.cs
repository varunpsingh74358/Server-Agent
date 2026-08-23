using CloudOrc.WatchdogAgent.Recovery;

namespace CloudOrc.WatchdogAgent.Tests.Recovery;

public class ConsecutiveFailureTrackerTests
{
    [Fact]
    public void NewTracker_StartsAtZero()
    {
        var tracker = new ConsecutiveFailureTracker();

        Assert.Equal(0, tracker.ConsecutiveFailures);
        Assert.False(tracker.HasReachedThreshold(1));
    }

    [Fact]
    public void RecordFailure_IncrementsCounter()
    {
        var tracker = new ConsecutiveFailureTracker();

        tracker.RecordFailure();
        tracker.RecordFailure();

        Assert.Equal(2, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void HasReachedThreshold_IsFalseBelowThreshold_TrueAtOrAboveIt()
    {
        var tracker = new ConsecutiveFailureTracker();

        tracker.RecordFailure();
        tracker.RecordFailure();
        Assert.False(tracker.HasReachedThreshold(3));

        tracker.RecordFailure();
        Assert.True(tracker.HasReachedThreshold(3));
    }

    [Fact]
    public void SingleTransientFailure_DoesNotReachDefaultThreshold()
    {
        // Mirrors the spec's requirement: one bad health check must never trigger recovery.
        var tracker = new ConsecutiveFailureTracker();

        tracker.RecordFailure();

        Assert.False(tracker.HasReachedThreshold(3));
    }

    [Fact]
    public void RecordSuccess_ResetsCounterAndReturnsPreviousValue()
    {
        var tracker = new ConsecutiveFailureTracker();
        tracker.RecordFailure();
        tracker.RecordFailure();

        var previous = tracker.RecordSuccess();

        Assert.Equal(2, previous);
        Assert.Equal(0, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void RecordSuccess_AfterReachingThreshold_ClearsThreshold()
    {
        var tracker = new ConsecutiveFailureTracker();
        tracker.RecordFailure();
        tracker.RecordFailure();
        tracker.RecordFailure();
        Assert.True(tracker.HasReachedThreshold(3));

        tracker.RecordSuccess();

        Assert.False(tracker.HasReachedThreshold(3));
    }
}
