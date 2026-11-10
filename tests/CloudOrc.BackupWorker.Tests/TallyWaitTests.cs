using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker.Consistency;

namespace CloudOrc.BackupWorker.Tests;

public class TallyWaitTests
{
    private static readonly IReadOnlyList<BackupSource> Tally = [new BackupSource { Path = @"D:\Tally", Kind = BackupSourceKinds.Tally }];

    private static BackupConsistencyPolicy Policy(string mode, int wait = 1) =>
        new() { Tally = new TallyConsistencyPolicy { Mode = mode, MaxWaitMinutes = wait } };

    [Fact]
    public async Task WaitForClose_ReturnsTrue_OnceTallyCloses()
    {
        var checks = 0;
        var closed = await ConsistencyPlanner.WaitForApplicationCloseAsync(Tally, Policy(TallyModes.WaitForClose), _ => ++checks < 3,
            TimeSpan.FromMilliseconds(10), null, CancellationToken.None);
        Assert.True(closed);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task WaitForClose_GivesUpAfterMaxWait_AndLogsWhy()
    {
        var logs = new List<string>();
        var closed = await ConsistencyPlanner.WaitForApplicationCloseAsync(Tally, Policy(TallyModes.WaitForClose, wait: 0), _ => true,
            TimeSpan.FromMilliseconds(10), logs.Add, CancellationToken.None);
        Assert.False(closed);
        Assert.Contains(logs, l => l.Contains("crash-consistent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherModes_NeverWait()
    {
        var checks = 0;
        await ConsistencyPlanner.WaitForApplicationCloseAsync(Tally, Policy(TallyModes.RequireClosed, wait: 60), _ => { checks++; return true; },
            TimeSpan.FromSeconds(30), null, CancellationToken.None);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void StillRunningAfterWait_IsCrashConsistent_AndDegraded()
    {
        var policy = Policy(TallyModes.WaitForClose);
        var plan = ConsistencyPlanner.Plan(Tally, policy, _ => new VolumeFacts("NTFS", 100, 50), _ => true);
        Assert.Equal(ConsistencyLevels.CrashConsistent, plan.ExpectedLevel);
        Assert.True(ConsistencyPlanner.IsDegraded(plan.ExpectedLevel, policy));
    }
}
