using Leistd.Timing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.Core.Tests;

/// <summary>框架的时间基准实现：<see cref="IClock.Now"/> 恒为 UTC，<see cref="IClock.Normalize"/> 是唯一入口。</summary>
public class UtcClockProviderTests
{
    [Fact]
    public void Now_is_always_utc_regardless_of_the_underlying_provider_offset()
    {
        // 时间源给的是带 +08:00 偏移的时刻，Now 必须换算成 UTC 而不是截掉偏移
        var provider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 4, 0, 0, TimeSpan.FromHours(8)));

        var now = new UtcClockProvider(provider).Now;

        Assert.Equal(new DateTime(2026, 5, 27, 20, 0, 0, DateTimeKind.Utc), now);
        Assert.Equal(DateTimeKind.Utc, now.Kind);
    }

    [Fact]
    public void Now_advances_with_the_time_provider()
    {
        var provider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clock = new UtcClockProvider(provider);

        var before = clock.Now;
        provider.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(TimeSpan.FromMinutes(90), clock.Now - before);
    }

    // Unspecified 一律当作 UTC：数据库读回的 DateTime 就是这个类型，
    // 若改按本地解释，同一份数据在不同时区的机器上会得出不同时刻。
    [Fact]
    public void Unspecified_is_taken_as_utc_without_shifting_the_value()
    {
        var input = new DateTime(2026, 5, 27, 20, 0, 0, DateTimeKind.Unspecified);

        var normalized = new UtcClockProvider(TimeProvider.System).Normalize(input);

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(input.Ticks, normalized.Ticks);
    }

    [Fact]
    public void Local_is_converted_rather_than_relabeled()
    {
        var local = new DateTime(2026, 5, 27, 20, 0, 0, DateTimeKind.Local);

        var normalized = new UtcClockProvider(TimeProvider.System).Normalize(local);

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(local.ToUniversalTime(), normalized);
    }

    [Fact]
    public void Utc_passes_through_unchanged()
    {
        var utc = new DateTime(2026, 5, 27, 20, 0, 0, DateTimeKind.Utc);

        Assert.Equal(utc, new UtcClockProvider(TimeProvider.System).Normalize(utc));
    }

    // 无参构造是给"宿主没注册 TimeProvider"的组合根用的；
    // 显式传 null 走的是同一条兜底，两者必须一致，否则 DI 注册方式会改变行为。
    [Fact]
    public void Missing_time_provider_falls_back_to_the_system_clock()
    {
        Assert.All(
            [new UtcClockProvider(), new UtcClockProvider(null!)],
            clock => Assert.InRange(
                clock.Now,
                DateTime.UtcNow.AddMinutes(-1),
                DateTime.UtcNow.AddMinutes(1)));
    }
}
