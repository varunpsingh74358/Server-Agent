namespace CloudOrc.WatchdogAgent.Recovery;

public sealed record RecoveryDecision(bool Allowed, string? Reason)
{
    public static RecoveryDecision Allow() => new(true, null);

    public static RecoveryDecision Deny(string reason) => new(false, reason);
}
