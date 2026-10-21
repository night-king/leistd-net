using Leistd.Timing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.Core.Tests;

/// <summary>自然日与时区偏移的换算：全框架"今天"的口径都由这两个扩展给出。</summary>
public class ClockExtensionsTests
{
    // 东八区不设夏令时，用来钉固定偏移下的日界；北美东部有夏令时，用来钉偏移随时刻变化。
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static IClock ClockAt(string utcInstant) =>
        new UtcClockProvider(new FakeTimeProvider(DateTimeOffset.Parse(utcInstant + "Z").ToUniversalTime()));

    // UTC 与目标时区跨日，验证日界取目标时区。
    [Fact]
    public void Midnight_follows_the_target_time_zone_across_the_utc_date_boundary()
    {
        var clock = ClockAt("2026-05-27T20:00:00");

        var midnight = clock.GetMidnightInUtc(Shanghai);

        Assert.Equal(new DateTime(2026, 5, 27, 16, 0, 0, DateTimeKind.Utc), midnight);
        Assert.Equal(DateTimeKind.Utc, midnight.Kind);
    }

    [Theory]
    // UTC 与目标时区同日：零点即当日 16:00Z 的前一个 16:00Z
    [InlineData("2026-05-27T04:00:00", "2026-05-26T16:00:00")]
    // 恰好落在时区零点那一刻：属于新的一天，不回退到前一天
    [InlineData("2026-05-27T16:00:00", "2026-05-27T16:00:00")]
    // 零点前一秒仍算前一天
    [InlineData("2026-05-27T15:59:59", "2026-05-26T16:00:00")]
    public void Midnight_boundary_instants_resolve_to_the_day_that_contains_them(string nowUtc, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), ClockAt(nowUtc).GetMidnightInUtc(Shanghai));
    }

    // 夏令时下偏移随时刻变化，不能缓存也不能按固定值算——同一个时区两个季节两个答案。
    [Theory]
    [InlineData("2026-01-15T12:00:00", -5)]   // 标准时
    [InlineData("2026-07-15T12:00:00", -4)]   // 夏令时
    public void Utc_offset_accounts_for_daylight_saving(string nowUtc, double expectedHours)
    {
        Assert.Equal(expectedHours, ClockAt(nowUtc).GetUtcOffsetHours(NewYork));
    }

    [Fact]
    public void Utc_offset_is_zero_for_utc_itself()
    {
        Assert.Equal(0d, ClockAt("2026-05-27T04:00:00").GetUtcOffsetHours(TimeZoneInfo.Utc));
    }

    // 半小时时区存在（印度 +5:30），偏移不是整数——按 int 算会静默丢掉 30 分钟。
    [Fact]
    public void Utc_offset_keeps_fractional_hours()
    {
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

        Assert.Equal(5.5d, ClockAt("2026-05-27T04:00:00").GetUtcOffsetHours(kolkata));
    }

    // 时钟给出的是本地或未指定类型时也必须先归一化再换算，否则日界会整体平移。
    [Fact]
    public void Non_utc_clock_values_are_normalized_before_conversion()
    {
        var midnight = new UnspecifiedKindClock(new DateTime(2026, 5, 27, 20, 0, 0, DateTimeKind.Unspecified))
            .GetMidnightInUtc(Shanghai);

        Assert.Equal(new DateTime(2026, 5, 27, 16, 0, 0, DateTimeKind.Utc), midnight);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var clock = ClockAt("2026-05-27T04:00:00");

        Assert.Throws<ArgumentNullException>(() => clock.GetMidnightInUtc(null!));
        Assert.Throws<ArgumentNullException>(() => clock.GetUtcOffsetHours(null!));
        Assert.Throws<ArgumentNullException>(() => ((IClock)null!).GetMidnightInUtc(Shanghai));
        Assert.Throws<ArgumentNullException>(() => ((IClock)null!).GetUtcOffsetHours(Shanghai));
    }

    /// <summary>返回 <see cref="DateTimeKind.Unspecified"/> 的时钟，用来钉归一化确实发生了。</summary>
    private sealed class UnspecifiedKindClock(DateTime now) : IClock
    {
        public DateTime Now { get; } = now;

        public DateTime Normalize(DateTime dateTime) => dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
        };
    }
}
