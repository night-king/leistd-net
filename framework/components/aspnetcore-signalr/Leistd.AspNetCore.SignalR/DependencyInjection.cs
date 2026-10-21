using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Leistd.AspNetCore.SignalR.Middlewares;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Leistd.AspNetCore.SignalR.Filters;
using Leistd.AspNetCore.SignalR.Options;
using Leistd.AspNetCore.SignalR.Services;
using Leistd.Security;

namespace Leistd.AspNetCore.SignalR;

/// <summary>SignalR 基座的注册入口：连接主体的解析、Hub 调用的环境上下文与有效性复检。</summary>
public static class DependencyInjection
{
    /// <summary>注册 SignalR、<see cref="AmbientContextHubFilter"/> 与 <see cref="ClaimsSignalRUserIdProvider"/>。</summary>
    /// <remarks>
    /// 幂等注册全局过滤器与非 HTTP 环境上下文，不覆盖宿主的 HubOptions。
    /// HTTP 主体读取另由 <c>AddSecurity()</c> 注册，与本方法的调用顺序无关。
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">连接主体解析与复检选项。</param>
    public static IServiceCollection AddSignalRAmbientContext(
        this IServiceCollection services,
        Action<HubIdentityOptions>? configure = null)
    {
        services.AddOptions<HubIdentityOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddAmbientContext();
        services.AddSignalR();
        UseClaimsUserIdProvider(services);

        // 多个上层组件可重复注册基座，用标记服务避免重复挂载上下文过滤器。
        if (services.Any(d => d.ServiceType == typeof(HubAmbientContextMarker)))
        {
            return services;
        }

        services.AddSingleton<HubAmbientContextMarker>();
        services.AddSingleton<AmbientContextHubFilter>();
        services.Configure<HubOptions>(options => options.AddFilter<AmbientContextHubFilter>());

        return services;
    }

    // 只替换 SignalR 自带的默认 IUserIdProvider，保留宿主自定义实现。
    // 不用 Replace（它移除首项并追加，可能改变宿主实现的优先级）：只在当前胜出的是默认实现时才换掉。
    private static void UseClaimsUserIdProvider(IServiceCollection services)
    {
        // 只看非 keyed 注册：SignalR 按非 keyed 解析，keyed 描述符的 ImplementationType 为 null 会被误判为宿主实现
        var winner = services.LastOrDefault(
            d => !d.IsKeyedService && d.ServiceType == typeof(IUserIdProvider));
        if (winner is not null && winner.ImplementationType != typeof(DefaultUserIdProvider))
        {
            return;
        }

        if (winner is not null)
        {
            services.Remove(winner);
        }

        services.AddSingleton<IUserIdProvider, ClaimsSignalRUserIdProvider>();
    }

    // 独立标记区分"本方法已注册过"与宿主自行添加的同类过滤器。
    private sealed class HubAmbientContextMarker;

    /// <summary>
    /// 要求 Hub 握手按 <see cref="HubIdentityOptions.PolicyName"/> 授权（未设置时按宿主的默认策略），
    /// 与 <see cref="AmbientContextHubFilter"/> 调用期复评取同一策略。
    /// </summary>
    /// <remarks>
    /// 组件的 <c>Map*Hub</c> 已经调用它；宿主自己映射的 Hub 也用它代替 <c>RequireAuthorization</c>，
    /// 否则宿主指定的策略只在握手或调用其中一个阶段生效。策略名在端点构建时从容器读取，与注册顺序无关。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.Configure&lt;HubIdentityOptions&gt;(options =&gt; options.PolicyName = "Realtime");
    ///
    /// app.MapHub&lt;OrderHub&gt;("/hubs/orders").RequireHubAuthorization();
    /// </code>
    /// </example>
    /// <param name="hub">Hub 端点约定构建器。</param>
    public static HubEndpointConventionBuilder RequireHubAuthorization(this HubEndpointConventionBuilder hub)
    {
        ArgumentNullException.ThrowIfNull(hub);

        hub.Add(endpoint =>
        {
            var policyName = endpoint.ApplicationServices.GetRequiredService<IOptions<HubIdentityOptions>>().Value.PolicyName;
            endpoint.Metadata.Add(policyName is null ? new AuthorizeAttribute() : new AuthorizeAttribute(policyName));
        });
        return hub;
    }

    /// <summary>在 Hub 端点上把查询串里的 <c>access_token</c> 转成 Bearer 请求头，供 JWT 等基于请求头的认证方案读取。</summary>
    /// <remarks>
    /// <para>只作用于 Hub 端点（按端点元数据识别），并把令牌从查询串移除；其它端点不接受查询串令牌，避免令牌进入访问日志与 Referer。</para>
    /// <para>必须在路由之后、认证之前调用；已带 <c>Authorization</c> 头的请求原样放行。Cookie 认证的宿主不需要它。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.UseRouting();
    /// app.UseHubAccessToken();
    /// app.UseAuthentication();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseHubAccessToken(this IApplicationBuilder app)
        => app.UseMiddleware<HubAccessTokenMiddleware>();
}
