namespace CloudOrc.ControlAgent.RdpTransferPolicy;

/// <summary>
/// Mirrors the exact numeric values Windows assigns to the "Restrict clipboard transfer
/// from client to server" / "...from server to client" Remote Desktop Session Host
/// policies (registry values LimitClientToServerClipboardRedirection /
/// LimitServerToClientClipboardRedirection, DWORD, under
/// HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services). Do not renumber these -
/// they must match what the OS itself reads/writes.
/// </summary>
public enum RdpClipboardRedirectionLevel
{
    Disabled = 0,
    PlainTextOnly = 1,
    PlainTextAndImages = 2,
    PlainTextImagesAndRtf = 3,
    PlainTextImagesRtfAndHtml = 4
}
