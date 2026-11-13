using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.ControlAgent.Tests.Backup;

/// <summary>Backup type: when a backup re-reads every file (full) instead of trusting the change cache.</summary>
public sealed class BackupModeTests
{
    private static readonly DateTimeOffset Sunday = new(2026, 11, 15, 22, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset Monday = Sunday.AddDays(1);

    [Fact]
    public void Incremental_IsTheDefault()
    {
        Assert.False(BackupModes.IsFullScan(null, null, null, Sunday));
        Assert.False(BackupModes.IsFullScan(null, BackupModes.Incremental, "Sunday", Sunday));
        Assert.Equal(BackupModes.Incremental, new BackupPolicyDocument
        {
            PolicyId = "p", PolicyVersionId = "v", EffectiveHash = "h",
            Schedule = new BackupSchedule(), Repository = new BackupRepositoryRef { RepositoryId = "r", StoragePoolId = "s", Engine = "kopia" }
        }.BackupMode);
    }

    [Fact]
    public void FullPolicy_IsAlwaysFull()
    {
        Assert.True(BackupModes.IsFullScan(null, BackupModes.Full, null, Monday));
    }

    [Fact]
    public void WeeklyFull_IsFullOnlyOnTheChosenDay()
    {
        Assert.True(BackupModes.IsFullScan(null, BackupModes.WeeklyFull, "Sunday", Sunday));
        Assert.False(BackupModes.IsFullScan(null, BackupModes.WeeklyFull, "Sunday", Monday));
        Assert.True(BackupModes.IsFullScan(null, BackupModes.WeeklyFull, "monday", Monday));
    }

    [Fact]
    public void PerRunRequest_WinsOverThePolicy()
    {
        Assert.True(BackupModes.IsFullScan(BackupModes.Full, BackupModes.Incremental, null, Monday));
        Assert.False(BackupModes.IsFullScan(BackupModes.Incremental, BackupModes.Full, null, Monday));
    }
}
