using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.RealTime.Subscriptions;

namespace Leistd.RealTime;

/// <summary>实时核心服务注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册实时核心能力。</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddRealTime();
    ///
    /// await businessEventPublisher.PublishToResourceAsync($"Products:{productId}", "Updated", payload, ct);
    /// </code>
    /// </example>
    /// <remarks>
    /// 不注册默认订阅授权器：公共资源场景显式注册 <see cref="AllowAllRealTimeSubscriptionAuthorizer"/>；
    /// 未注册时 <c>MapRealTimeHub()</c> 映射即失败。可重复调用，结果与调用一次相同。
    /// </remarks>
    public static IServiceCollection AddRealTime(this IServiceCollection services)
    {
        return services;
    }

    /// <summary>注册“允许订阅任意资源”的授权器，用于公共资源场景。</summary>
    /// <remarks>可重复调用；已注册其他订阅授权器时不覆盖，先注册者生效。</remarks>
    public static IServiceCollection AddAllowAllRealTimeSubscriptions(this IServiceCollection services)
    {
        services.TryAddSingleton<IRealTimeSubscriptionAuthorizer, AllowAllRealTimeSubscriptionAuthorizer>();
        return services;
    }

    /// <summary>注册“只允许订阅指定前缀资源”的授权器，如公共看板统一挂在 <c>public:</c> 下。</summary>
    /// <remarks>
    /// 前缀按序数比较；需要按用户或租户判定的资源不要用它，实现自己的 <see cref="IRealTimeSubscriptionAuthorizer"/>。
    /// 与 <see cref="AddAllowAllRealTimeSubscriptions"/> 二选一，先注册者生效；重复调用同样以首次的前缀为准，
    /// 不同前缀不合并，需要多个前缀时在一次调用中全部给出。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddPrefixRealTimeSubscriptions("public:");
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="prefixes">允许订阅的资源键前缀，至少一个且不得为空白。</param>
    public static IServiceCollection AddPrefixRealTimeSubscriptions(this IServiceCollection services, params string[] prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        if (prefixes.Length == 0 || prefixes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty resource key prefix is required.", nameof(prefixes));
        }

        services.TryAddSingleton<IRealTimeSubscriptionAuthorizer>(new PrefixRealTimeSubscriptionAuthorizer([.. prefixes]));
        return services;
    }
}
