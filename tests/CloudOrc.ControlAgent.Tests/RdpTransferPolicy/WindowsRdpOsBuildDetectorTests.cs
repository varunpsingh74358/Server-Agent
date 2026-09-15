using CloudOrc.ControlAgent.RdpTransferPolicy;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.RdpTransferPolicy;

/// <summary>
/// Covers the Windows build/UBR support matrix for the directional ("plain text only")
/// clipboard redirection policies. The matrix itself (<see cref="WindowsRdpOsBuildDetector.SupportsDirectionalClipboard"/>)
/// is a pure function extracted specifically so every OS scenario the master spec calls
/// out - Server 2019, Server 2022 above/below the required UBR, other Windows 11 builds,
/// an unrecognized future build - can be asserted deterministically without needing a
/// matching real machine for each one.
///
/// <see cref="DetectCurrent_OnAnyRealWindowsMachine_ReadsRegistryConsistentlyWithTheMatrix"/>
/// is the one genuine integration test here: it reads (never writes) this machine's actual
/// registry. It deliberately does NOT assert a specific build number - local dev boxes and
/// hosted CI runners (e.g. GitHub Actions' "windows-latest") are not guaranteed to be the
/// same Windows version/build, so it instead asserts internal consistency: whatever build
/// this machine happens to be, DetectCurrent()'s verdict must match the same pure matrix
/// function above, evaluated on that same build/UBR.
/// </summary>
public class WindowsRdpOsBuildDetectorTests
{
    [Theory]
    [InlineData(17763, 0, false)]      // Windows Server 2019 - never supported, at any UBR.
    [InlineData(17763, int.MaxValue, false)]
    [InlineData(20348, 2522, false)]   // Windows Server 2022 - one UBR below the required minimum.
    [InlineData(20348, 2523, true)]    // Windows Server 2022 - exactly the required minimum UBR.
    [InlineData(20348, 3000, true)]    // Windows Server 2022 - above the required minimum UBR.
    [InlineData(22000, 3013, false)]   // Windows 11 21H2 - one UBR below the required minimum.
    [InlineData(22000, 3014, true)]    // Windows 11 21H2 - exactly the required minimum UBR.
    [InlineData(26100, 0, true)]       // Windows 11 24H2 / Server 2025 - supported from RTM (UBR 0).
    [InlineData(99999, 500, false)]    // Unrecognized future build - never assumed supported.
    [InlineData(0, 0, false)]          // Build could not be determined at all.
    public void SupportsDirectionalClipboard_MatchesTheDocumentedSupportMatrix(int build, int ubr, bool expectedSupported)
    {
        var actual = WindowsRdpOsBuildDetector.SupportsDirectionalClipboard(build, ubr);

        Assert.Equal(expectedSupported, actual);
    }

    [Fact]
    public void DetectCurrent_OnAnyRealWindowsMachine_ReadsRegistryConsistentlyWithTheMatrix()
    {
        var detector = new WindowsRdpOsBuildDetector(NullLogger<WindowsRdpOsBuildDetector>.Instance);

        var info = detector.DetectCurrent();

        Assert.True(info.Build > 0, "Expected a real CurrentBuildNumber to be readable from the registry on any Windows machine.");
        Assert.NotEmpty(info.ProductName);
        Assert.Equal(WindowsRdpOsBuildDetector.SupportsDirectionalClipboard(info.Build, info.Ubr), info.SupportsDirectionalClipboard);
    }
}
