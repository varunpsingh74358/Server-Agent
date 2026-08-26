using System.Diagnostics;
using CloudOrc.WatchdogAgent.ControlAgentManagement;

namespace CloudOrc.WatchdogAgent.Tests.ControlAgentManagement;

public class ProcessResourceSamplerTests
{
    [Fact]
    public void ForCurrentProcess_FirstSample_ReportsRunningWithNullCpuPercent()
    {
        var sampler = ProcessResourceSampler.ForCurrentProcess();

        var snapshot = sampler.Sample();

        Assert.True(snapshot.IsRunning);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
        Assert.Null(snapshot.CpuPercent); // no prior sample yet to compute a delta from
        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.PrivateMemoryBytes > 0);
    }

    [Fact]
    public async Task ForCurrentProcess_SecondSampleAfterElapsedTime_ReportsNonNullCpuPercent()
    {
        var sampler = ProcessResourceSampler.ForCurrentProcess();
        sampler.Sample();

        await Task.Delay(50);
        var snapshot = sampler.Sample();

        Assert.True(snapshot.IsRunning);
        Assert.NotNull(snapshot.CpuPercent);
        Assert.InRange(snapshot.CpuPercent!.Value, 0, 100);
    }

    [Fact]
    public void ForProcessName_NoMatchingProcess_ReportsNotRunning()
    {
        var sampler = ProcessResourceSampler.ForProcessName("definitely-not-a-real-process-name-zzz");

        var snapshot = sampler.Sample();

        Assert.False(snapshot.IsRunning);
        Assert.Null(snapshot.ProcessId);
        Assert.Null(snapshot.CpuPercent);
    }

    [Fact]
    public void ForProcessName_MatchingRunningProcess_ReportsRunning()
    {
        var ownProcessName = Process.GetCurrentProcess().ProcessName;
        var sampler = ProcessResourceSampler.ForProcessName(ownProcessName);

        var snapshot = sampler.Sample();

        Assert.True(snapshot.IsRunning);
        Assert.True(snapshot.ProcessId > 0);
        Assert.True(snapshot.WorkingSetBytes > 0);
    }

    [Fact]
    public void Sample_ResolverAlwaysReturnsNull_AlwaysReportsNotRunning()
    {
        var sampler = new ProcessResourceSampler(() => null);

        Assert.False(sampler.Sample().IsRunning);
        Assert.False(sampler.Sample().IsRunning);
    }

    [Fact]
    public void Sample_SamePidAcrossCalls_ComputesCpuDelta()
    {
        var sampler = new ProcessResourceSampler(Process.GetCurrentProcess);

        var first = sampler.Sample();
        Thread.Sleep(50);
        var second = sampler.Sample();

        Assert.Null(first.CpuPercent);
        Assert.NotNull(second.CpuPercent);
    }

    [Fact]
    public void Sample_PidChangesBetweenCalls_TreatsSecondSampleAsAFreshBaseline()
    {
        // Simulates a Control Agent restart (new PID) between two Watchdog monitoring
        // cycles: the CPU delta from before the restart must not be compared against the
        // new process's CPU time, so the sample right after a PID change must come back
        // with CpuPercent = null again, exactly like a genuinely first-ever sample.
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 5",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            // Sample() disposes whatever the resolver hands it (matching how the real
            // ForProcessName/ForCurrentProcess factories always return a fresh, single-use
            // Process wrapper) - so this resolver hands over a fresh wrapper around the
            // child's PID rather than the test's own long-lived `child` reference, which
            // this test still owns and kills in `finally` below.
            var callCount = 0;
            var sampler = new ProcessResourceSampler(() =>
            {
                callCount++;
                return callCount == 1 ? Process.GetCurrentProcess() : Process.GetProcessById(child.Id);
            });

            var first = sampler.Sample();
            var second = sampler.Sample();

            Assert.True(first.IsRunning);
            Assert.True(second.IsRunning);
            Assert.NotEqual(first.ProcessId, second.ProcessId);
            Assert.Null(second.CpuPercent);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }
}
