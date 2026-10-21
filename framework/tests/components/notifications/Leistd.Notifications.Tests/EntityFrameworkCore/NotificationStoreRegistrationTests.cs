using Leistd.Timing;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Notifications.Publishing;
using Leistd.Notifications.Stores;
using Leistd.Notifications.EntityFrameworkCore;
using Leistd.Notifications.EntityFrameworkCore.Stores;
using Xunit;

namespace Leistd.Notifications.Tests.EntityFrameworkCore;

public class NotificationStoreRegistrationTests
{
    // 两个上下文各注册一次时 Microsoft DI 静默取最后一条：通知落进宿主没预期的那个库。
    [Fact]
    public void Registering_the_store_for_a_second_context_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddNotificationsEfCore<FirstDbContext>();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddNotificationsEfCore<SecondDbContext>());

        Assert.Contains("already registered", error.Message);
    }

    // 重复登记同一个上下文无害，按幂等处理。
    // 宿主提前把同一个实现登记成错误的生命周期：必须拒绝。放行的话紧随其后的 TryAdd 会因
    // "已有注册"不再补正确那条，框架最终保留宿主那个错的——EF 存储被登记成单例尤其糟，
    // 它会捕获作用域内的上下文。
    [Fact]
    public void A_host_registration_with_the_wrong_lifetime_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationStore, EfCoreNotificationStore<FirstDbContext>>();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddNotificationsEfCore<FirstDbContext>());

        Assert.Contains("Singleton", error.Message);
    }

    [Fact]
    public void Registering_the_same_context_twice_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddNotificationsEfCore<FirstDbContext>();
        services.AddNotificationsEfCore<FirstDbContext>();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(INotificationStore));
        Assert.Equal(typeof(EfCoreNotificationStore<FirstDbContext>), descriptor.ImplementationType);
        // 生命周期一并钉住：四家的保障要对称，否则误改一处不会红
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    /// <summary>只装持久化包（不装实时包）时，发布器也解析得出来。</summary>
    /// <remarks>
    /// 「只要通知历史、不要实时推送」是组件文档列出的支持组合，而发布器在 Core 里，
    /// 因此 EF 入口要接上同家族的 <c>AddNotifications()</c>（幂等）。
    /// 本用例刻意不装 SignalR 包——它自己也会调 <c>AddNotifications()</c>，装上就覆盖掉了判据。
    /// 发布器另需宿主注册的 <see cref="IClock"/>，跨组件依赖不由本包代注册。
    /// </remarks>
    [Fact]
    public void Persistence_alone_resolves_the_publisher_without_the_realtime_package()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddUnitOfWork()
            .AddUnitOfWorkEfCore()
            .AddSingleton<IClock, UtcClockProvider>()
            .AddNotificationsEfCore<FirstDbContext>();
        services.AddDbContext<FirstDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INotificationPublisher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INotificationStore>());
    }

    private sealed class FirstDbContext(DbContextOptions<FirstDbContext> options) : DbContext(options);

    private sealed class SecondDbContext(DbContextOptions<SecondDbContext> options) : DbContext(options);
}
