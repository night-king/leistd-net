using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.TestBase.Assertions;

/// <summary>服务注册数量、生命周期、实现与幂等性的断言。</summary>
/// <remarks>注册测试覆盖生命周期、重复调用，以及与相邻组件的覆盖或共存关系。</remarks>
public static class ServiceCollectionAssertions
{
    /// <summary>断言 <typeparamref name="TService"/> 恰好注册一次，且生命周期符合预期。</summary>
    public static ServiceDescriptor AssertSingle<TService>(
        this IServiceCollection services, ServiceLifetime lifetime)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TService));
        Assert.Equal(lifetime, descriptor.Lifetime);
        return descriptor;
    }

    /// <summary>断言 <typeparamref name="TService"/> 恰好注册一次，且由 <typeparamref name="TImplementation"/> 兑现。</summary>
    /// <remarks>
    /// 工厂注册（<c>ImplementationFactory</c>）拿不到实现类型，此时改为解析后断言实例类型——
    /// 本方法只覆盖按类型注册的情形，工厂注册请用 <see cref="AssertResolvesTo{TService, TImplementation}"/>。
    /// </remarks>
    public static void AssertImplementedBy<TService, TImplementation>(this IServiceCollection services)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(TService));
        Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
    }

    /// <summary>构建容器并断言 <typeparamref name="TService"/> 解析为 <typeparamref name="TImplementation"/>。</summary>
    /// <remarks>覆盖工厂注册与"后注册覆盖先注册"这两种描述符层面看不出结果的情形。</remarks>
    public static void AssertResolvesTo<TService, TImplementation>(this IServiceCollection services)
        where TService : notnull
    {
        using var provider = services.BuildServiceProvider();
        Assert.IsType<TImplementation>(provider.GetRequiredService<TService>());
    }

    /// <summary>断言 <typeparamref name="TService"/> 未被注册。</summary>
    /// <remarks>检查组件没有替宿主注册其他组件的基础设施。</remarks>
    public static void AssertNotRegistered<TService>(this IServiceCollection services) =>
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(TService));

    /// <summary>断言同一注册动作执行两次后，不产生重复的服务注册。</summary>
    /// <example>
    /// <code>
    /// ServiceCollectionAssertions.AssertIdempotent(services => services.AddSecurity());
    /// </code>
    /// </example>
    /// <remarks>
    /// 按服务类型与实现统计注册次数；Options 配置器按累加契约排除，本断言不验证配置委托的执行结果。
    /// </remarks>
    public static void AssertIdempotent(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();

        register(services);
        var afterFirst = Meaningful(services);

        register(services);

        var duplicates = Meaningful(services)
            .GroupBy(d => d)
            .Where(g => g.Count() > afterFirst.Count(d => d.Equals(g.Key)))
            .Select(g => g.Key)
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            "Repeated calls added new service registrations: " + string.Join("; ", duplicates.Select(d => $"{d.Service} -> {d.Implementation}")));

        static List<(string Service, string Implementation)> Meaningful(IServiceCollection services) =>
            [.. services
                .Where(d => !IsOptionsConfigurator(d.ServiceType))
                .Select(d => (
                    d.ServiceType.FullName ?? d.ServiceType.Name,
                    d.ImplementationType?.FullName
                        ?? d.ImplementationInstance?.GetType().FullName
                        ?? "<factory>"))];

        static bool IsOptionsConfigurator(Type serviceType) =>
            serviceType.IsGenericType
            && serviceType.GetGenericTypeDefinition() is var open
            && (open == typeof(IConfigureOptions<>)
                || open == typeof(IPostConfigureOptions<>)
                || open == typeof(IValidateOptions<>)
                || open == typeof(IOptionsChangeTokenSource<>));
    }
}
