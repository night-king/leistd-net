using Leistd.Timing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>
/// 基座登记的 <see cref="IClock"/> 取容器里的 <see cref="TimeProvider"/>：宿主或测试换掉时间源，
/// 应用时钟与 OpenIddict、认证处理器等同样从容器取时间的官方组件一起走。
/// </summary>
/// <remarks>
/// 登记的是实现类型，由容器在两个构造函数里挑能满足的那个；改成无参登记或工厂时，
/// 换掉 <see cref="TimeProvider"/> 后应用时钟仍走系统时间，没有任何报错——测试推进时间也推不动它。
/// </remarks>
public class ClockRegistrationTests
{
    [Fact]
    public void Clock_follows_the_registered_time_provider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddDddInfrastructure();
        using var provider = services.BuildServiceProvider();
        var clock = provider.GetRequiredService<IClock>();

        Assert.Equal(time.GetUtcNow().UtcDateTime, clock.Now);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(time.GetUtcNow().UtcDateTime, clock.Now);
    }

    [Fact]
    public void Clock_falls_back_to_system_time_without_a_registered_time_provider()
    {
        using var provider = new ServiceCollection().AddDddInfrastructure().BuildServiceProvider();
        var clock = provider.GetRequiredService<IClock>();

        var before = DateTime.UtcNow;
        var now = clock.Now;
        Assert.InRange(now, before, DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, now.Kind);
    }

    [Fact]
    public void A_clock_registered_by_the_host_is_kept()
    {
        var hostClock = new FixedClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<IClock>(hostClock);
        services.AddDddInfrastructure();
        using var provider = services.BuildServiceProvider();

        Assert.Same(hostClock, provider.GetRequiredService<IClock>());
    }

    // 只用作"宿主自己的实现"的身份标记，不参与时间推进
    private sealed class FixedClock : IClock
    {
        public DateTime Now => DateTime.UnixEpoch;
        public DateTime Normalize(DateTime dateTime) => dateTime;
    }
}
