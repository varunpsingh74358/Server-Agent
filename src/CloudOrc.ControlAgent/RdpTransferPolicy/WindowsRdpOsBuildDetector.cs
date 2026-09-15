using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudOrc.ControlAgent.RdpTransferPolicy;

/// <summary>
/// Reads the real Windows build number/UBR from the registry (the same values Windows
/// Update itself stamps after servicing) and checks them against Microsoft's documented
/// support matrix for the "Restrict clipboard redirection" policies. Builds not listed
/// below - including Windows Server 2019's 17763, at any UBR - never support the
/// directional plain-text-only mode; an unrecognized/unreadable build is also treated as
/// unsupported rather than assumed to work, per the "never report false success" rule.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRdpOsBuildDetector(ILogger<WindowsRdpOsBuildDetector> logger) : IRdpOsBuildDetector
{
    private const string CurrentVersionRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // Minimum UBR at which each build train shipped directional clipboard redirection
    // restriction support (source: Microsoft's "Restrict clipboard redirection" policy
    // documentation / Intune settings catalog support matrix).
    private static readonly IReadOnlyDictionary<int, int> MinimumUbrForDirectionalClipboard = new Dictionary<int, int>
    {
        [20348] = 2523, // Windows Server 2022
        [22000] = 3014, // Windows 11 21H2
        [22621] = 3672, // Windows 11 22H2
        [22631] = 3672, // Windows 11 23H2
        [25398] = 946,  // Windows Server, Azure Edition 23H2
        [26100] = 0,    // Windows 11 24H2 / Windows Server 2025
    };

    public RdpOsBuildInfo DetectCurrent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionRegistryPath);
            var productName = key?.GetValue("ProductName") as string ?? "Unknown Windows";
            var buildString = key?.GetValue("CurrentBuildNumber") as string;
            var ubr = key?.GetValue("UBR") is int u ? u : 0;

            if (!int.TryParse(buildString, out var build))
            {
                logger.LogWarning("Could not read a valid CurrentBuildNumber from the registry; treating the directional clipboard policy as unsupported.");
                return new RdpOsBuildInfo(productName, Build: 0, Ubr: 0, SupportsDirectionalClipboard: false);
            }

            return new RdpOsBuildInfo(productName, build, ubr, SupportsDirectionalClipboard(build, ubr));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Could not read Windows version information from the registry; treating the directional clipboard policy as unsupported.");
            return new RdpOsBuildInfo("Unknown Windows", Build: 0, Ubr: 0, SupportsDirectionalClipboard: false);
        }
    }

    /// <summary>
    /// Pure decision logic separated from the registry read above so the build/UBR support
    /// matrix (Server 2019 never, Server 2022 only from a specific UBR, etc.) is unit
    /// testable for every scenario without needing a matching real OS for each one.
    /// </summary>
    public static bool SupportsDirectionalClipboard(int build, int ubr) =>
        MinimumUbrForDirectionalClipboard.TryGetValue(build, out var minimumUbr) && ubr >= minimumUbr;
}
