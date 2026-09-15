using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.RdpTransferPolicy;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.RdpTransferPolicy;

/// <summary>
/// Exercises <see cref="WindowsRdpTransferPolicyManager"/>'s apply/verify orchestration
/// end to end using <see cref="FakeRdpPolicyRegistryAccessor"/> and
/// <see cref="FakeRdpOsBuildDetector"/> instead of real HKLM writes or a matching real OS
/// for every build scenario - these fakes are what make it safe to run this suite on any
/// machine (including this repo's own Windows Server 2019 dev/CI box) without mutating
/// its actual RDP security policy. Real-registry-touching coverage is intentionally
/// limited to read-only checks (see WindowsRdpOsBuildDetectorTests); a test that
/// genuinely needs to observe a real HKLM write landing would have to run as a manual,
/// admin-elevated integration test on a real Windows Server box, which is out of scope
/// for the default automated suite.
/// </summary>
public class WindowsRdpTransferPolicyManagerTests
{
    private static CommandRequest Request(string commandId = "rdp-cmd-1") => new()
    {
        CommandId = commandId,
        CommandType = CommandTypes.RdpTransferPolicyApply,
        ClientToServerClipboard = "PLAIN_TEXT_ONLY",
        ServerToClientClipboard = "PLAIN_TEXT_ONLY",
        DriveRedirection = false
    };

    private static WindowsRdpTransferPolicyManager CreateManager(FakeRdpPolicyRegistryAccessor registry, bool supportsDirectionalClipboard) =>
        new(registry, new FakeRdpOsBuildDetector(supportsDirectionalClipboard), NullLogger<WindowsRdpTransferPolicyManager>.Instance);

    [Fact]
    public async Task ApplyAsync_SupportedBuild_AppliesClientToServerPlainTextOnly()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(RdpClipboardRedirectionLevel.PlainTextOnly, registry.ClientToServerClipboardLevel);
    }

    [Fact]
    public async Task ApplyAsync_SupportedBuild_AppliesServerToClientPlainTextOnly()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(RdpClipboardRedirectionLevel.PlainTextOnly, registry.ServerToClientClipboardLevel);
    }

    [Fact]
    public async Task ApplyAsync_DriveRedirectionIsBlocked()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.True(registry.DriveRedirectionBlocked);
    }

    [Fact]
    public async Task ApplyAsync_SupportedBuild_AllPoliciesVerified_ReturnsSuccess()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        var outcome = await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task ApplyAsync_OutputIncludesReadBackActualState()
    {
        // Verification is not a side note - the outcome must show what was actually read
        // back from the policy store, not just that a write call was attempted.
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        var outcome = await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Contains(outcome.Output, line => line.Contains("clientToServerClipboard") && line.Contains("actual=PlainTextOnly"));
        Assert.Contains(outcome.Output, line => line.Contains("driveRedirection") && line.Contains("actual=blocked"));
    }

    [Fact]
    public async Task ApplyAsync_UnsupportedBuild_DriveBlockSucceeds_ReturnsPartial()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: false);

        var outcome = await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(CommandStatus.Partial, outcome.Status);
        Assert.True(registry.DriveRedirectionBlocked);
        Assert.Contains(outcome.Output, line => line.Contains("clientToServerClipboard=UNSUPPORTED"));
    }

    [Fact]
    public async Task ApplyAsync_UnsupportedBuild_NeverAttemptsToWriteClipboardPolicy()
    {
        // An unsupported policy must never be silently swapped for a different/weaker
        // mechanism - it must simply not be written at all.
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: false);

        await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(0, registry.ClientToServerWriteCount);
        Assert.Equal(0, registry.ServerToClientWriteCount);
    }

    [Fact]
    public async Task ApplyAsync_UnsupportedBuild_DriveBlockAlsoFails_ReturnsFailed()
    {
        var registry = new FakeRdpPolicyRegistryAccessor { ThrowOnDriveWrite = true };
        var manager = CreateManager(registry, supportsDirectionalClipboard: false);

        var outcome = await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task ApplyAsync_SupportedBuild_VerificationMismatch_ReturnsFailed_NotPartial()
    {
        // The registry accessor's write "succeeds" (throws nothing) but the fake is
        // configured to read back a different value than requested - simulating an OS/API
        // reporting success while the actual policy did not take effect. Per spec this is
        // FAILED, distinct from the "categorically unsupported" Partial case.
        var registry = new FakeRdpPolicyRegistryAccessor { PinnedClientToServerReadValue = RdpClipboardRedirectionLevel.Disabled };
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        var outcome = await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, outcome.Status);
        Assert.Contains("did not read back as", outcome.Error);
    }

    [Fact]
    public async Task ApplyAsync_RegistryWriteThrows_ReturnsFailed_NeverSuccess()
    {
        var registry = new FakeRdpPolicyRegistryAccessor { ThrowOnClientToServerWrite = true };
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        var outcome = await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, outcome.Status);
        Assert.NotEqual(CommandStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task ApplyAsync_AppliedTwice_SecondCallIsIdempotent_DoesNotRewriteAlreadyCorrectValues()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        var first = await manager.ApplyAsync(Request("rdp-cmd-1"), CancellationToken.None);
        var second = await manager.ApplyAsync(Request("rdp-cmd-2"), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, first.Status);
        Assert.Equal(CommandStatus.Success, second.Status);
        Assert.Equal(1, registry.ClientToServerWriteCount);
        Assert.Equal(1, registry.ServerToClientWriteCount);
        Assert.Equal(1, registry.DriveWriteCount);
    }

    [Fact]
    public async Task ApplyAsync_FirstApply_RequiresNewSessionIsTrue()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        var outcome = await manager.ApplyAsync(Request(), CancellationToken.None);

        Assert.True(outcome.RequiresNewSession);
    }

    [Fact]
    public async Task ApplyAsync_SecondApplyWithNothingToChange_RequiresNewSessionIsFalse()
    {
        var registry = new FakeRdpPolicyRegistryAccessor();
        var manager = CreateManager(registry, supportsDirectionalClipboard: true);

        await manager.ApplyAsync(Request("rdp-cmd-1"), CancellationToken.None);
        var second = await manager.ApplyAsync(Request("rdp-cmd-2"), CancellationToken.None);

        Assert.False(second.RequiresNewSession);
    }

    private sealed class FakeRdpOsBuildDetector(bool supportsDirectionalClipboard) : IRdpOsBuildDetector
    {
        public RdpOsBuildInfo DetectCurrent() => new("Fake Windows", 20348, 2523, supportsDirectionalClipboard);
    }

    /// <summary>In-memory stand-in for the real registry, with hooks to simulate failures and read-back mismatches.</summary>
    private sealed class FakeRdpPolicyRegistryAccessor : IRdpPolicyRegistryAccessor
    {
        public RdpClipboardRedirectionLevel? ClientToServerClipboardLevel { get; private set; }

        public RdpClipboardRedirectionLevel? ServerToClientClipboardLevel { get; private set; }

        public bool DriveRedirectionBlocked { get; private set; }

        public int ClientToServerWriteCount { get; private set; }

        public int ServerToClientWriteCount { get; private set; }

        public int DriveWriteCount { get; private set; }

        public bool ThrowOnClientToServerWrite { get; init; }

        public bool ThrowOnDriveWrite { get; init; }

        public RdpClipboardRedirectionLevel? PinnedClientToServerReadValue { get; init; }

        public RdpClipboardRedirectionLevel? ReadClientToServerClipboardLevel() => PinnedClientToServerReadValue ?? ClientToServerClipboardLevel;

        public RdpClipboardRedirectionLevel? ReadServerToClientClipboardLevel() => ServerToClientClipboardLevel;

        public bool ReadDriveRedirectionBlocked() => DriveRedirectionBlocked;

        public void WriteClientToServerClipboardLevel(RdpClipboardRedirectionLevel level)
        {
            if (ThrowOnClientToServerWrite)
            {
                throw new UnauthorizedAccessException("Simulated registry write failure.");
            }

            ClientToServerClipboardLevel = level;
            ClientToServerWriteCount++;
        }

        public void WriteServerToClientClipboardLevel(RdpClipboardRedirectionLevel level)
        {
            ServerToClientClipboardLevel = level;
            ServerToClientWriteCount++;
        }

        public void WriteDriveRedirectionBlocked(bool blocked)
        {
            if (ThrowOnDriveWrite)
            {
                throw new UnauthorizedAccessException("Simulated registry write failure.");
            }

            DriveRedirectionBlocked = blocked;
            DriveWriteCount++;
        }
    }
}
