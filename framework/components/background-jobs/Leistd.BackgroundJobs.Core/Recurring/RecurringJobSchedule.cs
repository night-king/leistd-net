namespace Leistd.BackgroundJobs.Recurring;

/// <summary>周期任务的排期：把时间切成首尾相接的时段，每个时段执行一次。</summary>
/// <remarks>时段按 UTC 对齐到固定起点，不同副本与重启前后算出同一时段。</remarks>
public abstract class RecurringJobSchedule
{
    private protected RecurringJobSchedule()
    {
    }

    /// <summary>每隔固定时长执行一次，时段从 UTC 纪元起按该时长对齐。</summary>
    /// <param name="interval">间隔，不小于 1 秒。</param>
    public static RecurringJobSchedule Every(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromSeconds(1));

        return new IntervalSchedule(interval);
    }

    /// <summary>每天在指定的 UTC 时刻执行一次。</summary>
    /// <param name="timeOfDayUtc">UTC 时刻。</param>
    public static RecurringJobSchedule DailyAt(TimeOnly timeOfDayUtc) => new DailySchedule(timeOfDayUtc);

    /// <summary>返回 <paramref name="now"/> 所在时段的起点。</summary>
    /// <param name="now">当前时刻。</param>
    public abstract DateTimeOffset GetSlot(DateTimeOffset now);

    /// <summary>返回 <paramref name="now"/> 之后的下一个执行时刻（下一时段的起点）。</summary>
    /// <param name="now">当前时刻。</param>
    public abstract DateTimeOffset GetNextRun(DateTimeOffset now);

    /// <summary>进程启动后首次执行前的最大随机延迟：按间隔排期的任务在此范围内先跑一次；按每日时刻排期的为零，只在排定时刻执行。</summary>
    public abstract TimeSpan MaxStartupJitter { get; }

    private sealed class IntervalSchedule(TimeSpan interval) : RecurringJobSchedule
    {
        public override DateTimeOffset GetSlot(DateTimeOffset now)
        {
            var utc = now.ToUniversalTime();
            return new DateTimeOffset(utc.UtcTicks - utc.UtcTicks % interval.Ticks, TimeSpan.Zero);
        }

        public override DateTimeOffset GetNextRun(DateTimeOffset now) => GetSlot(now) + interval;

        public override TimeSpan MaxStartupJitter =>
            interval < TimeSpan.FromSeconds(30) ? interval : TimeSpan.FromSeconds(30);

        public override string ToString() => $"every {interval}";
    }

    private sealed class DailySchedule(TimeOnly timeOfDayUtc) : RecurringJobSchedule
    {
        public override DateTimeOffset GetSlot(DateTimeOffset now)
        {
            var today = TodayAt(now);
            return now.ToUniversalTime() >= today ? today : today.AddDays(-1);
        }

        public override DateTimeOffset GetNextRun(DateTimeOffset now) => GetSlot(now).AddDays(1);

        public override TimeSpan MaxStartupJitter => TimeSpan.Zero;

        private DateTimeOffset TodayAt(DateTimeOffset now)
        {
            var utc = now.ToUniversalTime();
            return new DateTimeOffset(DateOnly.FromDateTime(utc.UtcDateTime), timeOfDayUtc, TimeSpan.Zero);
        }

        public override string ToString() => $"daily at {timeOfDayUtc:HH\\:mm} UTC";
    }
}
