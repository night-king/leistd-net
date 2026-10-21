using Leistd.BackgroundJobs.Recurring;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>排期把时间切成按 UTC 对齐的时段：不同副本、重启前后算出的是同一个时段。</summary>
/// <remarks>
/// 时段若相对进程启动时间计算，两个副本的"同一时段"就对不上，集群水位比对失效，任务会在每个副本各跑一遍。
/// </remarks>
public class RecurringJobScheduleTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Theory]
    [InlineData("2026-09-20T10:07:30Z", "2026-09-20T10:05:00Z", "2026-09-20T10:10:00Z")]
    [InlineData("2026-09-20T10:05:00Z", "2026-09-20T10:05:00Z", "2026-09-20T10:10:00Z")]
    [InlineData("2026-09-20T18:07:30+08:00", "2026-09-20T10:05:00Z", "2026-09-20T10:10:00Z")]
    public void An_interval_schedule_aligns_slots_to_the_utc_epoch(string now, string slot, string next)
    {
        var schedule = RecurringJobSchedule.Every(TimeSpan.FromMinutes(5));

        Assert.Equal(At(slot), schedule.GetSlot(At(now)));
        Assert.Equal(At(next), schedule.GetNextRun(At(now)));
    }

    [Theory]
    [InlineData("2026-09-20T17:59:59Z", "2026-09-19T18:00:00Z", "2026-09-20T18:00:00Z")]
    [InlineData("2026-09-20T18:00:00Z", "2026-09-20T18:00:00Z", "2026-09-21T18:00:00Z")]
    [InlineData("2026-09-20T23:30:00Z", "2026-09-20T18:00:00Z", "2026-09-21T18:00:00Z")]
    public void A_daily_schedule_slots_run_from_one_time_of_day_to_the_next(string now, string slot, string next)
    {
        var schedule = RecurringJobSchedule.DailyAt(new TimeOnly(18, 0));

        Assert.Equal(At(slot), schedule.GetSlot(At(now)));
        Assert.Equal(At(next), schedule.GetNextRun(At(now)));
    }

    [Fact]
    public void A_sub_second_interval_is_rejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => RecurringJobSchedule.Every(TimeSpan.FromMilliseconds(500)));
        Assert.Equal("interval", exception.ParamName);
    }

    [Fact]
    public void A_one_second_interval_is_accepted()
    {
        var schedule = RecurringJobSchedule.Every(TimeSpan.FromSeconds(1));

        Assert.Equal(At("2026-09-20T10:07:31Z"), schedule.GetNextRun(At("2026-09-20T10:07:30.500Z")));
    }
}
