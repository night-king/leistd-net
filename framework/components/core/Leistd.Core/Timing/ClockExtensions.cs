namespace Leistd.Timing;

/// <summary>业务自然日到 UTC 时刻的转换扩展；时区由调用方显式传入，不默认使用宿主时区。</summary>
public static class ClockExtensions
{
    /// <summary>获取指定时区今日零点对应的 UTC 时刻。</summary>
    /// <remarks>
    /// 例：时区为 <c>Asia/Shanghai</c>、当前 UTC 为 <c>2026-05-27T20:00:00Z</c> 时，
    /// 该时区的当日是 05-28，返回 <c>2026-05-27T16:00:00Z</c>。
    /// </remarks>
    /// <param name="clock">时钟。</param>
    /// <param name="timeZone">用于判定“今日”的时区。</param>
    /// <returns><see cref="DateTimeKind.Utc"/> 的时刻。</returns>
    public static DateTime GetMidnightInUtc(this IClock clock, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timeZone);

        var nowUtc = DateTime.SpecifyKind(clock.Normalize(clock.Now), DateTimeKind.Utc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);

        // 本地零点是 Unspecified 的墙钟时间，按给定时区折回 UTC
        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified),
            timeZone);
    }

    /// <summary>取指定时区当前相对 UTC 的偏移小时数（已计入夏令时）。</summary>
    /// <param name="clock">时钟。</param>
    /// <param name="timeZone">目标时区。</param>
    public static double GetUtcOffsetHours(this IClock clock, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timeZone);

        var nowUtc = DateTime.SpecifyKind(clock.Normalize(clock.Now), DateTimeKind.Utc);
        return timeZone.GetUtcOffset(nowUtc).TotalHours;
    }
}
