using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.ResourceGovernor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.ResourceGovernor;

public class CoreBudgetMonitorTests
{
    private static CoreBudgetMonitor Create(CoreBudgetOptions budget) =>
        new(Options.Create(new BackupOptions { CoreBudget = budget }), new ServiceCollection().BuildServiceProvider(), NullLogger<CoreBudgetMonitor>.Instance);

    [Fact]
    public void Sample_MeasuresCore_AndTrimsWhenIdleAboveTarget()
    {
        var monitor = Create(new CoreBudgetOptions { IdleWorkingSetTargetMb = 1, MaxPrivateMb = 100_000, MaxCpuPercent = 100 });

        monitor.SampleAndTrim();
        var second = monitor.SampleAndTrim();

        Assert.True(second.WorkingSetBytes > 0);
        Assert.True(second.PrivateBytes > 0);
        Assert.NotNull(second.CpuPercent);
        Assert.False(second.OverBudget);
        Assert.Same(second, monitor.Latest);
    }

    [Fact]
    public void BudgetBreach_IsFlagged_NeverEnforcedByKilling()
    {
        var monitor = Create(new CoreBudgetOptions { IdleWorkingSetTargetMb = 100_000, MaxPrivateMb = 1 });
        Assert.True(monitor.SampleAndTrim().OverBudget);
    }
}
