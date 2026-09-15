namespace CloudOrc.ControlAgent.RdpTransferPolicy;

/// <summary>
/// Thin read/write access to the three RDS policy registry values this feature manages.
/// Contains no apply-vs-desired-state decisions of its own (that belongs to
/// <see cref="WindowsRdpTransferPolicyManager"/>) - it exists only so that orchestration
/// logic can be unit-tested against an in-memory fake instead of real HKLM writes, which
/// would mutate this machine's actual RDP security policy every time the test suite runs.
/// </summary>
public interface IRdpPolicyRegistryAccessor
{
    RdpClipboardRedirectionLevel? ReadClientToServerClipboardLevel();

    RdpClipboardRedirectionLevel? ReadServerToClientClipboardLevel();

    bool ReadDriveRedirectionBlocked();

    void WriteClientToServerClipboardLevel(RdpClipboardRedirectionLevel level);

    void WriteServerToClientClipboardLevel(RdpClipboardRedirectionLevel level);

    void WriteDriveRedirectionBlocked(bool blocked);
}
