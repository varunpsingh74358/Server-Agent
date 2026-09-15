namespace CloudOrc.ControlAgent.RdpTransferPolicy;

/// <summary>
/// The fixed desired state for CommandTypes.RdpTransferPolicyApply: plain-text-only
/// clipboard in both directions, drive redirection blocked. There is only one desired
/// state today (CommandRequestValidator rejects any other combination before this is ever
/// constructed) - this type exists so the apply/verify code compares against a named
/// value instead of scattered literals, not to support configurability that doesn't exist.
/// </summary>
public sealed record RdpTransferDesiredState
{
    public static readonly RdpTransferDesiredState PlainTextOnlyClipboardAndBlockedDrive = new();

    public RdpClipboardRedirectionLevel ClipboardLevel { get; } = RdpClipboardRedirectionLevel.PlainTextOnly;

    public bool DriveRedirectionBlocked { get; } = true;
}

/// <summary>
/// What the Windows registry actually reports right now for each of the three policies.
/// A null clipboard level means the policy value is absent/not configured (OS default:
/// unrestricted), as distinct from an explicit <see cref="RdpClipboardRedirectionLevel.Disabled"/>.
/// </summary>
public sealed record RdpTransferActualState(
    RdpClipboardRedirectionLevel? ClientToServerClipboardLevel,
    RdpClipboardRedirectionLevel? ServerToClientClipboardLevel,
    bool DriveRedirectionBlocked);

/// <summary>Windows build identity plus whether it supports the directional plain-text-only clipboard policies.</summary>
public sealed record RdpOsBuildInfo(string ProductName, int Build, int Ubr, bool SupportsDirectionalClipboard)
{
    public string FullVersion => $"10.0.{Build}.{Ubr}";
}
