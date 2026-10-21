using Leistd.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.DependencyInjection.Tests;

/// <summary>「同一服务只能有一个权威实现」断言的支持边界。</summary>
/// <remarks>
/// 这是对外 NuGet 公共 API，四个存储包都靠它。「工厂/实例/keyed 各算不算冲突」
/// 这几条边界在这里集中钉住，不依赖各消费组件的用例。
/// </remarks>
public class SingleAuthoritativeServiceTests
{
    private const string Reason = "Test services have a single authoritative store.";

    [Fact]
    public void An_empty_collection_passes()
    {
        var services = new ServiceCollection();

        services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason);
    }

    // 重复登记同一实现是幂等的：断言只管"有没有别的实现"，不管登记了几次。
    [Fact]
    public void The_same_implementation_registered_again_passes()
    {
        var services = new ServiceCollection();
        services.AddTransient<IStore, PrimaryStore>();

        services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason);
    }

    [Fact]
    public void A_different_implementation_type_conflicts()
    {
        var services = new ServiceCollection();
        services.AddTransient<IStore, OtherStore>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason));

        Assert.Contains(nameof(OtherStore), exception.Message);
        Assert.Contains(Reason, exception.Message);
    }

    // 实例注册能问出真实类型，但它的生命周期恒为单例：只有目标也是单例时才放行。
    [Fact]
    public void An_instance_of_the_same_type_passes_when_singleton_is_expected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStore>(new PrimaryStore());

        services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Singleton, Reason);
    }

    [Fact]
    public void An_instance_conflicts_when_a_non_singleton_lifetime_is_expected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStore>(new PrimaryStore());

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason));

        Assert.Contains("Singleton", exception.Message);
    }

    // 同一实现、错误生命周期：断言必须拒绝。放行的话紧随其后的 TryAdd 会因"已有注册"
    // 不再补正确那条，框架最终保留宿主那个错的——而 EF 存储被登记成单例会捕获作用域上下文。
    [Fact]
    public void The_same_implementation_with_a_different_lifetime_conflicts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStore, PrimaryStore>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason));

        Assert.Contains("Singleton", exception.Message);
        Assert.Contains("Transient", exception.Message);
    }

    [Fact]
    public void An_instance_of_a_different_type_conflicts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStore>(new OtherStore());

        Assert.Throws<InvalidOperationException>(
            () => services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason));
    }

    // 工厂注册问不出实现身份。对一个只能有一个权威实现的服务，"不确定"不能当成"没问题"。
    [Fact]
    public void A_factory_registration_conflicts_because_its_identity_cannot_be_proven()
    {
        var services = new ServiceCollection();
        services.AddTransient<IStore>(_ => new PrimaryStore());

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason));

        Assert.Contains("<factory>", exception.Message);
    }

    // keyed 注册按键解析，不参与单服务解析，不构成冲突。
    [Fact]
    public void A_keyed_registration_is_not_a_conflict()
    {
        var services = new ServiceCollection();
        services.AddKeyedTransient<IStore, OtherStore>("secondary");

        services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason);
    }

    // 必须枚举全部描述符：只看第一条会在"第一条恰是本类型、后面还有别的"时放行，
    // 而单服务解析实际胜出的是后者。
    [Fact]
    public void A_conflicting_registration_after_a_matching_one_is_still_caught()
    {
        var services = new ServiceCollection();
        services.AddTransient<IStore, PrimaryStore>();
        services.AddTransient<IStore, OtherStore>();

        Assert.Throws<InvalidOperationException>(
            () => services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason));
    }

    // 另一个服务上的实现与本次判定无关。
    [Fact]
    public void A_registration_for_another_service_is_ignored()
    {
        var services = new ServiceCollection();
        services.AddTransient<IOtherService, OtherStore>();

        services.EnsureSingleAuthoritative<IStore, PrimaryStore>(ServiceLifetime.Transient, Reason);
    }

    private interface IStore;

    private interface IOtherService;

    private sealed class PrimaryStore : IStore;

    private sealed class OtherStore : IStore, IOtherService;
}
