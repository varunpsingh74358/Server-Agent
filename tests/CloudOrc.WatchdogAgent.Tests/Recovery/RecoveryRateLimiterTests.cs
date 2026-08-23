using CloudOrc.WatchdogAgent.Configuration;
using CloudOrc.WatchdogAgent.Recovery;

namespace CloudOrc.WatchdogAgent.Tests.Recovery;

public class RecoveryRateLimiterTests
{
    private static WatchdogOptions MakeOptions(
        int maxAttemptsPerWindow = 3,
        int windowMinutes = 15,
        int initialBackoffSeconds = 30,
        int maxBackoffSeconds = 600) => new()
    {
        MaxRestartAttemptsPerWindow = maxAttemptsPerWindow,
        RestartRateLimitWindowMinutes = windowMinutes,
        InitialBackoffSeconds = initialBackoffSeconds,
        MaxBackoffSeconds = maxBackoffSeconds
    };

    [Fact]
    public void Evaluate_WithNoPriorAttempts_Allows()
    {
        var limiter = new RecoveryRateLimiter(MakeOptions(), new ManualTimeProvider(DateTimeOffset.UtcNow));

        var decision = limiter.Evaluate();

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Evaluate_AfterReachingMaxAttemptsInWindow_Denies()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new RecoveryRateLimiter(MakeOptions(maxAttemptsPerWindow: 2, initialBackoffSeconds: 0), time);

        limiter.RecordAttempt();
        limiter.RecordOutcome(succeeded: true);
        time.Advance(TimeSpan.FromSeconds(1));

        limiter.RecordAttempt();
        limiter.RecordOutcome(succeeded: true);
        time.Advance(TimeSpan.FromSeconds(1));

        var decision = limiter.Evaluate();

        Assert.False(decision.Allowed);
        Assert.Contains("Rate limit", decision.Reason);
    }

    [Fact]
    public void Evaluate_AfterWindowExpires_AllowsAgain()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new RecoveryRateLimiter(MakeOptions(maxAttemptsPerWindow: 1, windowMinutes: 10, initialBackoffSeconds: 0), time);

        limiter.RecordAttempt();
        limiter.RecordOutcome(succeeded: true);

        Assert.False(limiter.Evaluate().Allowed);

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.True(limiter.Evaluate().Allowed);
    }

    [Fact]
    public void Evaluate_ImmediatelyAfterFailedAttempt_DeniesUntilBackoffElapses()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new RecoveryRateLimiter(MakeOptions(initialBackoffSeconds: 30), time);

        limiter.RecordAttempt();
        limiter.RecordOutcome(succeeded: false);

        var decision = limiter.Evaluate();
        Assert.False(decision.Allowed);
        Assert.Contains("Backing off", decision.Reason);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(limiter.Evaluate().Allowed);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.Evaluate().Allowed);
    }

    [Fact]
    public void Backoff_DoublesWithEachConsecutiveFailure_UpToMax()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new RecoveryRateLimiter(
            MakeOptions(maxAttemptsPerWindow: 100, initialBackoffSeconds: 10, maxBackoffSeconds: 50), time);

        limiter.RecordOutcome(succeeded: false); // 1st failure -> backoff = 10s
        Assert.Equal(TimeSpan.FromSeconds(10), limiter.ComputeBackoff());

        limiter.RecordOutcome(succeeded: false); // 2nd consecutive failure -> backoff = 20s
        Assert.Equal(TimeSpan.FromSeconds(20), limiter.ComputeBackoff());

        limiter.RecordOutcome(succeeded: false); // 3rd -> 40s
        Assert.Equal(TimeSpan.FromSeconds(40), limiter.ComputeBackoff());

        limiter.RecordOutcome(succeeded: false); // 4th -> would be 80s, capped at 50s
        Assert.Equal(TimeSpan.FromSeconds(50), limiter.ComputeBackoff());
    }

    [Fact]
    public void RecordOutcome_Success_ResetsBackoffToZero()
    {
        var limiter = new RecoveryRateLimiter(MakeOptions(), new ManualTimeProvider(DateTimeOffset.UtcNow));

        limiter.RecordOutcome(succeeded: false);
        limiter.RecordOutcome(succeeded: false);
        Assert.True(limiter.ComputeBackoff() > TimeSpan.Zero);

        limiter.RecordOutcome(succeeded: true);

        Assert.Equal(TimeSpan.Zero, limiter.ComputeBackoff());
    }

    [Fact]
    public void RepeatedFailedRecoveries_EventuallyStayDeniedByBackoff_PreventingAggressiveLoop()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new RecoveryRateLimiter(
            MakeOptions(maxAttemptsPerWindow: 100, initialBackoffSeconds: 60, maxBackoffSeconds: 600), time);

        limiter.RecordAttempt();
        limiter.RecordOutcome(succeeded: false);

        // Immediately trying again must be denied - this is the core anti-flood guarantee.
        Assert.False(limiter.Evaluate().Allowed);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(limiter.Evaluate().Allowed);
    }
}
