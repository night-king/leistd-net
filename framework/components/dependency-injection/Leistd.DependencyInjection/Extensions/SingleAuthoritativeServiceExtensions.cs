using Microsoft.Extensions.DependencyInjection;

namespace Leistd.DependencyInjection.Extensions;

/// <summary>“同一服务只能有一个权威实现”的注册期断言。</summary>
public static class SingleAuthoritativeServiceExtensions
{
    /// <summary>验证已有的非 keyed 注册均匹配预期实现类型与生命周期。</summary>
    /// <remarks>
    /// <para>用于只能有一个权威实现的存储，不用于允许宿主替换的 Manager 等服务。
    /// 本方法只验证，不登记服务；通过后使用 <c>TryAdd*</c> 注册。</para>
    /// <para>检查全部匹配描述符。支持封闭类型与实例注册；实例只在预期为
    /// <see cref="ServiceLifetime.Singleton"/> 时通过。工厂无法确定实现身份，按冲突拒绝。
    /// keyed 与开放泛型注册不在检查范围内。</para>
    /// </remarks>
    /// <typeparam name="TService">服务契约。</typeparam>
    /// <typeparam name="TImplementation">本次要登记的实现类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="expectedLifetime">本次要登记的生命周期；已有注册的生命周期必须与之一致。</param>
    /// <param name="reason">面向宿主的一句话，说明为什么这个服务只能有一个权威实现。</param>
    /// <exception cref="InvalidOperationException">
    /// 已存在另一个实现、同一实现但生命周期不同，或存在无法确定身份的工厂注册。
    /// </exception>
    public static IServiceCollection EnsureSingleAuthoritative<TService, TImplementation>(
        this IServiceCollection services,
        ServiceLifetime expectedLifetime,
        string reason)
        where TImplementation : class, TService
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(TService) || descriptor.IsKeyedService)
            {
                continue;
            }

            // 实例注册能问出真实类型；工厂注册问不出来，返回 null 按冲突处理。
            var registered = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
            if (registered == typeof(TImplementation) && descriptor.Lifetime == expectedLifetime)
            {
                continue;
            }

            var actual = registered?.FullName ?? "<factory>";
            throw new InvalidOperationException(
                $"An {typeof(TService).Name} is already registered as '{actual}' " +
                $"({descriptor.Lifetime}). {reason} Registering " +
                $"'{typeof(TImplementation).FullName}' ({expectedLifetime}) as well would silently win or " +
                "lose by ordering; and an existing registration with a different lifetime is kept as-is by " +
                "TryAdd, so the framework would end up honouring that one.");
        }

        return services;
    }
}
