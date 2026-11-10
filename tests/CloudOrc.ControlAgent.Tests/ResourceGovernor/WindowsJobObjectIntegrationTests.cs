using System.Diagnostics;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.ResourceGovernor;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.ResourceGovernor;

/// <summary>
/// Real Windows Job Object tests (no fakes): proves the P/Invoke structures are correct and that
/// the caps are actually applied to a live process tree.
/// </summary>
public class WindowsJobObjectIntegrationTests
{
    private static Process StartIdleChild()
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var start = new ProcessStartInfo(cmd, "/c ping -n 30 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true };
        return Process.Start(start)!;
    }

    [Fact]
    public void Configure_Assign_Query_AndLiveCpuChange_AllSucceed()
    {
        var budget = ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4L << 30);
        using var job = new WindowsJobObject();
        job.Configure(budget);

        using var child = StartIdleChild();
        try
        {
            job.Assign(child);
            Thread.Sleep(500);

            var usage = job.QueryUsage();
            Assert.True(usage.ActiveProcesses >= 1, "the child (and ping) must be inside the job");
            Assert.True(usage.WorkingSetBytes > 0);

            job.SetCpuRate(5); // live re-throttle, as the adaptive controller does
            job.SetCpuRate(budget.MaxCpuPercent);
        }
        finally
        {
            try { child.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void PriorityClass_IsAppliedToProcessesInTheJob()
    {
        var budget = ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4L << 30); // Idle priority
        using var job = new WindowsJobObject();
        job.Configure(budget);
        using var child = StartIdleChild();
        try
        {
            job.Assign(child);
            child.Refresh();
            Assert.Equal(ProcessPriorityClass.Idle, child.PriorityClass);
        }
        finally
        {
            try { child.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void DisposingTheJob_KillsTheWholeTree()
    {
        var job = new WindowsJobObject();
        job.Configure(ResourceProfiles.Resolve(ResourceProfileNames.Balanced, 4, 8L << 30));
        using var child = StartIdleChild();
        job.Assign(child);

        job.Dispose(); // KILL_ON_JOB_CLOSE

        Assert.True(child.WaitForExit(10_000), "an orphaned worker must not keep running without Agent Core");
    }

    [Fact]
    public void DiskBandwidthCap_OnTheSystemVolume_IsAcceptedOrReportedUnavailable()
    {
        using var job = new WindowsJobObject();
        job.Configure(ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4L << 30));
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;

        // Must never throw: either applied, or false so the governor logs and relies on CPU caps.
        var applied = job.SetDiskBandwidth([systemRoot], 25L * 1024 * 1024);

        // Either it really applied, or the OS reported a real error - never a silent "success".
        // (Windows Server 2019 / 17763 reports ERROR_INVALID_FUNCTION = 1 here.)
        if (!applied)
        {
            Assert.NotEqual(0, job.LastDiskControlError);
        }
    }

    [Fact]
    public async Task GovernorSession_SamplesRealPressure_AndProducesSection7Metrics()
    {
        var options = new ResourceGovernorOptions { SampleIntervalSeconds = 1 };
        var budget = ResourceProfiles.Resolve(ResourceProfileNames.Eco, 2, 4L << 30);
        using var monitor = new SystemPressureMonitor(options);
        var session = new GovernorSession(budget, new WindowsJobObject(), monitor, options, [Path.GetPathRoot(Environment.SystemDirectory)!], NullLogger.Instance);
        using var child = StartIdleChild();
        try
        {
            session.Start(child);
            session.UploadBytesPerSecond = 1234;
            session.Tick();
            await Task.Delay(1100);
            session.Tick();

            var sample = session.LatestSample!;
            Assert.Equal(ResourceProfileNames.Eco, sample.Profile);
            Assert.True(sample.WorkerWorkingSetBytes > 0);
            Assert.True(sample.AgentCoreWorkingSetBytes > 0);
            Assert.Equal(1234, sample.UploadBytesPerSecond);
            Assert.NotNull(sample.WorkerCpuPercent);

            // The session must know whether the disk cap is real on this OS, so the fallback kicks in.
            using var probe = new WindowsJobObject();
            probe.Configure(budget);
            Assert.Equal(probe.SetDiskBandwidth([Path.GetPathRoot(Environment.SystemDirectory)!], budget.MaxDiskBytesPerSecond), session.DiskCapAvailable);
        }
        finally
        {
            await session.DisposeAsync();
            Assert.True(child.WaitForExit(10_000));
        }
    }
}
