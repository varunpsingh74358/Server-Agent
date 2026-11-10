using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.ResourceGovernor;

namespace CloudOrc.ControlAgent.Tests.ResourceGovernor;

public class ResourceProfilesTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Eco_On2vCpu4Gb_IsSmallIdleAndAllowsAdaptivePause()
    {
        var eco = ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4 * Gb);

        Assert.Equal(1, eco.Parallelism);
        Assert.Equal(1, eco.GoMaxProcs);
        Assert.Equal(JobPriority.Idle, eco.Priority);
        Assert.True(eco.AllowAdaptivePause);
        Assert.True(eco.YieldToProtectedApps);
        Assert.True(eco.MaxMemoryBytes <= 4 * Gb / 5);
        Assert.True(eco.GoMemoryLimitMb * 1024L * 1024 < eco.MaxMemoryBytes, "Go soft limit must sit inside the hard cap.");
    }

    [Fact]
    public void Throughput_UsesMoreParallelismThanBalanced_ThanEco()
    {
        var eco = ResourceProfiles.Resolve(ResourceProfileNames.Eco, 16, 64 * Gb);
        var balanced = ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 16, 64 * Gb);
        var throughput = ResourceProfiles.Resolve(ResourceProfileNames.Throughput, 16, 64 * Gb);

        Assert.True(eco.Parallelism < balanced.Parallelism);
        Assert.True(balanced.Parallelism < throughput.Parallelism);
        Assert.True(eco.MaxCpuPercent < balanced.MaxCpuPercent && balanced.MaxCpuPercent < throughput.MaxCpuPercent);
    }

    [Fact]
    public void UnknownProfile_FallsBackToEco() =>
        Assert.Equal(ResourceProfileNames.Eco, ResourceProfiles.Resolve("turbo", 4, 8 * Gb).Profile);

    [Fact]
    public void Lease_StricterLimitsWin()
    {
        var balanced = ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 8, 16 * Gb);
        var leased = ResourceProfiles.ApplyLease(balanced, new AdmissionLease
        {
            LeaseId = "l", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), MaxParallel = 1, MaxUploadMbps = 8
        });

        Assert.Equal(1, leased.Parallelism);
        Assert.Equal(1, leased.GoMaxProcs);
        Assert.Equal(1_000_000, leased.MaxUploadBytesPerSecond); // 8 Mbit/s

        var eco = ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4 * Gb);
        var ecoLeased = ResourceProfiles.ApplyLease(eco, new AdmissionLease { LeaseId = "l", ExpiresAt = DateTimeOffset.UtcNow, MaxUploadMbps = 1000 });
        Assert.Equal(eco.MaxUploadBytesPerSecond, ecoLeased.MaxUploadBytesPerSecond); // profile cap is stricter
    }
}

public class AdaptiveThrottleControllerTests
{
    private static readonly ResourceGovernorOptions Options = new() { RecoverAfterHealthySamples = 3 };
    private static readonly PressureSample Calm = new(10, 2, 4000, false);
    private static readonly PressureSample SlowDisk = new(10, 80, 4000, false);

    [Fact]
    public void DiskPressure_CutsDiskReadsFirst_ThenCpu()
    {
        var budget = ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8L << 30);
        var controller = new AdaptiveThrottleController(budget, Options);

        var (level1, changed1) = controller.Evaluate(SlowDisk);
        Assert.True(changed1);
        Assert.Equal(1, level1.Level);
        Assert.Equal(budget.MaxCpuPercent, level1.CpuPercent);              // CPU untouched at level 1
        Assert.True(level1.DiskBytesPerSecond < budget.MaxDiskBytesPerSecond); // reads slowed first

        var (level2, _) = controller.Evaluate(SlowDisk);
        Assert.Equal(2, level2.Level);
        Assert.True(level2.CpuPercent < budget.MaxCpuPercent);
    }

    [Fact]
    public void OnlyEco_MayReachNearPause()
    {
        var eco = new AdaptiveThrottleController(ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4L << 30), Options);
        var balanced = new AdaptiveThrottleController(ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 2, 4L << 30), Options);
        for (var i = 0; i < 6; i++)
        {
            eco.Evaluate(SlowDisk);
            balanced.Evaluate(SlowDisk);
        }

        Assert.Equal(3, eco.Level);
        Assert.Equal(2, balanced.Level);
    }

    [Fact]
    public void Relaxes_OneLevel_AfterConsecutiveHealthySamples()
    {
        var controller = new AdaptiveThrottleController(ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8L << 30), Options);
        controller.Evaluate(SlowDisk);
        controller.Evaluate(SlowDisk);
        Assert.Equal(2, controller.Level);

        controller.Evaluate(Calm);
        controller.Evaluate(Calm);
        Assert.Equal(2, controller.Level);
        controller.Evaluate(Calm);
        Assert.Equal(1, controller.Level);
    }

    [Fact]
    public void Eco_YieldsToActiveTally_AndHoldsLevelOneWhileItRuns()
    {
        var controller = new AdaptiveThrottleController(ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4L << 30), Options);
        var tallyActive = Calm with { ProtectedAppActive = true };

        controller.Evaluate(tallyActive);
        Assert.Equal(1, controller.Level);

        for (var i = 0; i < 10; i++)
        {
            controller.Evaluate(tallyActive);
        }

        Assert.Equal(1, controller.Level); // never relaxes to 0 while Tally is in use

        for (var i = 0; i < 3; i++)
        {
            controller.Evaluate(Calm);
        }

        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void Balanced_IgnoresTallyAlone()
    {
        var controller = new AdaptiveThrottleController(ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8L << 30), Options);
        controller.Evaluate(Calm with { ProtectedAppActive = true });
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void WithoutDiskCaps_LevelOneSlowsReadsThroughTheCpuCap()
    {
        var budget = ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8L << 30);
        var controller = new AdaptiveThrottleController(budget, Options);
        var (level1, _) = controller.Evaluate(SlowDisk);

        Assert.Equal(budget.MaxCpuPercent, controller.ForPlatform(level1, diskCapAvailable: true).CpuPercent);
        Assert.True(controller.ForPlatform(level1, diskCapAvailable: false).CpuPercent < budget.MaxCpuPercent);

        var level0 = controller.SettingsFor(0);
        Assert.Equal(level0, controller.ForPlatform(level0, diskCapAvailable: false)); // no pressure → untouched
    }

    [Fact]
    public void MissingCounters_DoNotTriggerThrottling()
    {
        var controller = new AdaptiveThrottleController(ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4L << 30), Options);
        controller.Evaluate(new PressureSample(null, null, null, false));
        Assert.Equal(0, controller.Level);
    }
}
