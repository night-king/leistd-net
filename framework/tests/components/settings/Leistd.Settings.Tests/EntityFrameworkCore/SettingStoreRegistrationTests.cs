using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.EntityFrameworkCore;
using Leistd.Settings.EntityFrameworkCore.Stores;
using Xunit;

namespace Leistd.Settings.Tests.EntityFrameworkCore;

public class SettingStoreRegistrationTests
{
    // 两个上下文各注册一次时 Microsoft DI 静默取最后一条：设置落进宿主没预期的那个库。
    [Fact]
    public void Registering_the_store_for_a_second_context_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddSettingsEfCore<FirstDbContext>();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddSettingsEfCore<SecondDbContext>());

        Assert.Contains("already registered", error.Message);
    }

    // 宿主提前把同一个实现登记成错误的生命周期：必须拒绝。放行的话紧随其后的 TryAdd 会因
    // "已有注册"不再补正确那条，框架最终honour宿主那个错的——EF 存储被登记成单例尤其糟，
    // 它会捕获作用域内的上下文。
    [Fact]
    public void A_host_registration_with_the_wrong_lifetime_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingStore, EfCoreSettingStore<FirstDbContext>>();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddSettingsEfCore<FirstDbContext>());

        Assert.Contains("Singleton", error.Message);
    }

    [Fact]
    public void Registering_the_same_context_twice_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddSettingsEfCore<FirstDbContext>();
        services.AddSettingsEfCore<FirstDbContext>();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ISettingStore));
        Assert.Equal(typeof(EfCoreSettingStore<FirstDbContext>), descriptor.ImplementationType);
        // 生命周期一并钉住：四家的保障要对称，否则误改一处不会红
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    // 缺存储时解析解析器应立即失败，而不是静默只返回代码默认值。
    [Fact]
    public void Resolving_the_provider_without_a_store_fails()
    {
        var sp = new ServiceCollection().AddSettingsCore().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<ISettingProvider>());
    }

    // keyed 注册按键解析，不参与 ISettingStore 的单服务解析，不构成"两个权威存储"。
    // 把它当冲突会让宿主没法用键旁挂一个只读镜像之类的东西。
    [Fact]
    public void A_keyed_store_is_not_treated_as_a_conflict()
    {
        var services = new ServiceCollection();
        services.AddKeyedTransient<ISettingStore, ThrowingSettingStore>("mirror");

        services.AddSettingsEfCore<FirstDbContext>();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ISettingStore) && !d.IsKeyedService);
        Assert.Equal(typeof(EfCoreSettingStore<FirstDbContext>), descriptor.ImplementationType);
    }

    // 只看第一条会漏掉这种排布：第一条恰是本类型，后面还藏着另一个实现，
    // 而单服务解析由最后一条胜出——放行等于让"单一权威存储"的承诺落空。
    [Fact]
    public void A_foreign_store_registered_after_this_one_is_still_rejected()
    {
        var services = new ServiceCollection();
        services.AddSettingsEfCore<FirstDbContext>();
        services.AddTransient<ISettingStore, ThrowingSettingStore>();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddSettingsEfCore<FirstDbContext>());

        Assert.Contains("already registered", error.Message);
    }

    private sealed class ThrowingSettingStore : ISettingStore
    {
        public bool CanAccessHostScope => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(
            Definitions.SettingScopes scope,
            string? userId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetAsync(
            string name,
            string? value,
            Definitions.SettingScopes scope,
            string? userId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FirstDbContext(DbContextOptions<FirstDbContext> options) : DbContext(options);

    private sealed class SecondDbContext(DbContextOptions<SecondDbContext> options) : DbContext(options);
}
