using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Backup;

namespace CloudOrc.ControlAgent.Tests.Backup;

public class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void StableOffset_IsDeterministic_WithinWindow_AndDiffersAcrossAgents()
    {
        var a1 = ScheduleCalculator.StableOffset("agent-a", "pol", 120);
        var a2 = ScheduleCalculator.StableOffset("agent-a", "pol", 120);
        var offsets = Enumerable.Range(0, 50).Select(i => ScheduleCalculator.StableOffset($"agent-{i}", "pol", 120)).ToList();

        Assert.Equal(a1, a2);
        Assert.All(offsets, o => Assert.InRange(o, TimeSpan.Zero, TimeSpan.FromMinutes(120)));
        Assert.True(offsets.Distinct().Count() > 40, "Offsets should spread the fleet across the window.");
        Assert.Equal(TimeSpan.Zero, ScheduleCalculator.StableOffset("x", "y", 0));
    }

    [Fact]
    public void Daily_DueSlotStartsAtSlotPlusOffset()
    {
        var schedule = new BackupSchedule { Kind = BackupScheduleKinds.Daily, TimesOfDay = ["01:00"] };
        var offset = TimeSpan.FromMinutes(30);
        var lastSlot = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);

        // 01:20 - offset not yet elapsed → nothing new.
        Assert.Null(ScheduleCalculator.Evaluate(schedule, new DateTimeOffset(2026, 11, 9, 1, 20, 0, TimeSpan.Zero), Utc, lastSlot, offset, 0.5));

        var due = ScheduleCalculator.Evaluate(schedule, new DateTimeOffset(2026, 11, 9, 1, 35, 0, TimeSpan.Zero), Utc, lastSlot, offset, 0.5);
        Assert.NotNull(due);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), due!.Slot);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 1, 30, 0, TimeSpan.Zero), due.NotBefore);
        Assert.False(due.IsCatchUp);
    }

    [Fact]
    public void Daily_SlotAlreadyHandled_IsNotDueAgain()
    {
        var schedule = new BackupSchedule { Kind = BackupScheduleKinds.Daily, TimesOfDay = ["01:00"] };
        var slot = new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
        Assert.Null(ScheduleCalculator.Evaluate(schedule, slot.AddMinutes(5), Utc, slot, TimeSpan.Zero, 0));
    }

    [Fact]
    public void MissedSlot_IsCaughtUpOnce_WithRandomSpread()
    {
        var schedule = new BackupSchedule { Kind = BackupScheduleKinds.Daily, TimesOfDay = ["01:00"], CatchUpSpreadMinutes = 60 };
        var now = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero); // agent was off until 09:00

        var due = ScheduleCalculator.Evaluate(schedule, now, Utc, new DateTimeOffset(2026, 11, 7, 1, 0, 0, TimeSpan.Zero), TimeSpan.Zero, 0.5);

        Assert.NotNull(due);
        Assert.True(due!.IsCatchUp);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero), due.Slot); // only the latest slot
        Assert.Equal(now.AddMinutes(30), due.NotBefore);
    }

    [Fact]
    public void MissedSlot_WithRunMissedFalse_IsRecordedButNotRun()
    {
        var schedule = new BackupSchedule { Kind = BackupScheduleKinds.Daily, TimesOfDay = ["01:00"], RunMissed = false };
        var due = ScheduleCalculator.Evaluate(schedule, new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), Utc, null, TimeSpan.Zero, 0);
        Assert.Equal(DateTimeOffset.MaxValue, due!.NotBefore);
    }

    [Fact]
    public void Daily_DaysOfWeekAreHonoured()
    {
        var schedule = new BackupSchedule { Kind = BackupScheduleKinds.Daily, TimesOfDay = ["01:00"], DaysOfWeek = ["Saturday"] };
        // 2026-11-09 is a Monday → latest slot is Saturday 2026-11-07.
        var slot = ScheduleCalculator.LatestSlotAtOrBefore(schedule, new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 1, 0, 0, TimeSpan.Zero), slot);
    }

    [Fact]
    public void Interval_SlotsAreEpochAnchored()
    {
        var schedule = new BackupSchedule { Kind = BackupScheduleKinds.Interval, IntervalHours = 6 };
        var slot = ScheduleCalculator.LatestSlotAtOrBefore(schedule, new DateTimeOffset(2026, 11, 9, 13, 30, 0, TimeSpan.Zero), Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), slot);
    }

    [Fact]
    public void Manual_IsNeverScheduled()
    {
        var schedule = new BackupSchedule { Kind = BackupScheduleKinds.Manual };
        Assert.Null(ScheduleCalculator.Evaluate(schedule, DateTimeOffset.UtcNow, Utc, null, TimeSpan.Zero, 0));
    }
}

public class BackupPolicyValidatorTests
{
    [Fact]
    public void ValidPolicy_HasNoErrors() => Assert.Empty(BackupPolicyValidator.Validate(BackupTestData.Policy()));

    [Theory]
    [InlineData(@"\\fileserver\share\data")]
    [InlineData(@"\\?\C:\Data")]
    [InlineData(@"Data\relative")]
    [InlineData(@"C:\Data\..\Windows")]
    [InlineData(@"C:\Data:stream")]
    [InlineData("")]
    public void UnsafeSourcePaths_AreRejected(string path)
    {
        var policy = BackupTestData.Policy(sources: [new BackupSource { Path = path }]);
        Assert.NotEmpty(BackupPolicyValidator.Validate(policy));
    }

    [Fact]
    public void BadSchedule_UnknownEngine_AndZeroRetention_AreRejected()
    {
        var policy = new BackupPolicyDocument
        {
            PolicyId = "p",
            PolicyVersionId = "v",
            EffectiveHash = "h",
            Sources = [new BackupSource { Path = @"C:\Data" }],
            Schedule = new BackupSchedule { Kind = BackupScheduleKinds.Daily, TimesOfDay = ["25:99"] },
            Retention = new BackupRetention { KeepLatest = 0, KeepDaily = null, KeepWeekly = null, KeepMonthly = null },
            Repository = new BackupRepositoryRef { RepositoryId = "r", StoragePoolId = "s", Engine = "zip" }
        };

        var errors = BackupPolicyValidator.Validate(policy);
        Assert.Contains(errors, e => e.Contains("timesOfDay", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("engine", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("retention", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("job-1", true)]
    [InlineData("a:b.c_d-e", true)]
    [InlineData("has space", false)]
    [InlineData("../etc", false)]
    [InlineData("", false)]
    public void SafeIds(string id, bool expected) => Assert.Equal(expected, BackupPolicyValidator.IsSafeId(id));
}
