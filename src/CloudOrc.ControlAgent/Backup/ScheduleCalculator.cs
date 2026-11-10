using System.Security.Cryptography;
using System.Text;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>A nominal schedule slot and when this agent should actually start it.</summary>
public sealed record DueRun(DateTimeOffset Slot, DateTimeOffset NotBefore, bool IsCatchUp);

/// <summary>
/// Pure schedule maths (no I/O, unit-testable).
///
/// §8: "Stable per-agent schedule offset prevents midnight synchronization; post-outage recovery
/// uses independent randomized spread." Each (agent, policy) pair gets a deterministic offset in
/// [0, windowMinutes) - the same agent always starts at the same time, but a fleet with the same
/// "01:00" policy is spread over the window. A slot that was missed (agent off, or offline long
/// past its start) is caught up once, at a fresh random point in the catch-up spread, so a
/// fleet coming back after an outage does not stampede either.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>A slot whose start time passed more than this long ago is "missed", not "due".</summary>
    public static readonly TimeSpan MissedThreshold = TimeSpan.FromMinutes(15);

    public static TimeSpan StableOffset(string agentId, string policyId, int windowMinutes)
    {
        if (windowMinutes <= 0)
        {
            return TimeSpan.Zero;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{agentId}|{policyId}"));
        var value = BitConverter.ToUInt64(hash, 0);
        return TimeSpan.FromSeconds(value % (ulong)(windowMinutes * 60));
    }

    /// <summary>The most recent nominal slot at or before <paramref name="now"/>, or null (manual / none in the last 8 days).</summary>
    public static DateTimeOffset? LatestSlotAtOrBefore(BackupSchedule schedule, DateTimeOffset now, TimeZoneInfo zone)
    {
        switch (schedule.Kind)
        {
            case BackupScheduleKinds.Interval when schedule.IntervalHours is > 0:
            {
                // Anchored to the Unix epoch so slots are stable across restarts and machines.
                var interval = TimeSpan.FromHours(schedule.IntervalHours.Value).Ticks;
                var sinceEpoch = (now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / interval * interval;
                return new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + sinceEpoch, TimeSpan.Zero);
            }

            case BackupScheduleKinds.Daily:
            {
                var times = schedule.TimesOfDay
                    .Select(t => BackupPolicyValidator.TryParseTimeOfDay(t, out var ts) ? ts : (TimeSpan?)null)
                    .Where(t => t is not null).Select(t => t!.Value)
                    .OrderByDescending(t => t)
                    .ToList();
                if (times.Count == 0)
                {
                    return null;
                }

                var days = schedule.DaysOfWeek
                    .Select(d => Enum.TryParse<DayOfWeek>(d, true, out var day) ? day : (DayOfWeek?)null)
                    .Where(d => d is not null).Select(d => d!.Value)
                    .ToHashSet();

                var localNow = TimeZoneInfo.ConvertTime(now, zone);
                for (var back = 0; back <= 8; back++)
                {
                    var date = localNow.Date.AddDays(-back);
                    if (days.Count > 0 && !days.Contains(date.DayOfWeek))
                    {
                        continue;
                    }

                    foreach (var time in times)
                    {
                        var localSlot = date + time;
                        if (zone.IsInvalidTime(localSlot))
                        {
                            localSlot = localSlot.AddHours(1); // DST spring-forward gap
                        }

                        var slot = new DateTimeOffset(localSlot, zone.GetUtcOffset(localSlot));
                        if (slot <= now)
                        {
                            return slot.ToUniversalTime();
                        }
                    }
                }

                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Decides whether a new run is due. <paramref name="lastSlot"/> is the last slot a job was
    /// already created for. <paramref name="random01"/> is injected for deterministic tests.
    /// </summary>
    public static DueRun? Evaluate(
        BackupSchedule schedule,
        DateTimeOffset now,
        TimeZoneInfo zone,
        DateTimeOffset? lastSlot,
        TimeSpan stableOffset,
        double random01)
    {
        if (schedule.Kind == BackupScheduleKinds.Manual)
        {
            return null;
        }

        // A slot only becomes "ours" once its stable offset has elapsed.
        var slot = LatestSlotAtOrBefore(schedule, now - stableOffset, zone);
        if (slot is null || (lastSlot is not null && slot <= lastSlot))
        {
            return null;
        }

        var start = slot.Value + stableOffset;
        if (now - start <= MissedThreshold)
        {
            return new DueRun(slot.Value, start, IsCatchUp: false);
        }

        if (!schedule.RunMissed)
        {
            // Record the slot as handled without running it.
            return new DueRun(slot.Value, DateTimeOffset.MaxValue, IsCatchUp: true);
        }

        var spread = TimeSpan.FromMinutes(Math.Max(0, schedule.CatchUpSpreadMinutes) * Math.Clamp(random01, 0, 1));
        return new DueRun(slot.Value, now + spread, IsCatchUp: true);
    }

    public static TimeZoneInfo ResolveZone(BackupSchedule schedule) =>
        schedule.TimeZoneId is not null && BackupPolicyValidator.TryFindTimeZone(schedule.TimeZoneId, out var zone) ? zone : TimeZoneInfo.Local;
}
