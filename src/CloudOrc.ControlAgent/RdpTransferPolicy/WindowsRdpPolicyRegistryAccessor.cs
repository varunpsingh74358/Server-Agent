using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CloudOrc.ControlAgent.RdpTransferPolicy;

/// <summary>
/// The actual Windows-native mechanism behind this feature: these are the same registry
/// values Group Policy/Intune write for the Remote Desktop Session Host "Device and
/// Resource Redirection" policies (Administrative Templates > Windows Components > Remote
/// Desktop Services > Remote Desktop Session Host > Device and Resource Redirection).
/// Writing them directly under HKLM\SOFTWARE\Policies\... - the local-policy override of
/// the same key a domain GPO would populate - is how RDS-aware components (termsrv,
/// rdpcorekmts) actually read this configuration; there is no separate Win32 API call for
/// "set clipboard/drive redirection policy" beyond this.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRdpPolicyRegistryAccessor : IRdpPolicyRegistryAccessor
{
    private const string TerminalServicesPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string ClientToServerClipboardValueName = "LimitClientToServerClipboardRedirection";
    private const string ServerToClientClipboardValueName = "LimitServerToClientClipboardRedirection";
    private const string DriveRedirectionValueName = "fDisableCdm";

    public RdpClipboardRedirectionLevel? ReadClientToServerClipboardLevel() => ReadClipboardLevel(ClientToServerClipboardValueName);

    public RdpClipboardRedirectionLevel? ReadServerToClientClipboardLevel() => ReadClipboardLevel(ServerToClientClipboardValueName);

    public bool ReadDriveRedirectionBlocked() => ReadDword(DriveRedirectionValueName) == 1;

    public void WriteClientToServerClipboardLevel(RdpClipboardRedirectionLevel level) => WriteDword(ClientToServerClipboardValueName, (int)level);

    public void WriteServerToClientClipboardLevel(RdpClipboardRedirectionLevel level) => WriteDword(ServerToClientClipboardValueName, (int)level);

    public void WriteDriveRedirectionBlocked(bool blocked) => WriteDword(DriveRedirectionValueName, blocked ? 1 : 0);

    private static RdpClipboardRedirectionLevel? ReadClipboardLevel(string valueName)
    {
        var raw = ReadDword(valueName);
        return raw is int value && Enum.IsDefined(typeof(RdpClipboardRedirectionLevel), value)
            ? (RdpClipboardRedirectionLevel)value
            : null;
    }

    private static int? ReadDword(string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(TerminalServicesPolicyPath);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    private static void WriteDword(string valueName, int value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(TerminalServicesPolicyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open or create the registry key 'HKLM\\{TerminalServicesPolicyPath}'.");
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }
}
