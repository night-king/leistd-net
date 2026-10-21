using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.AspNetCore.SignalR;
using Leistd.AspNetCore.SignalR.Options;
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
using Leistd.RealTime.AspNetCore.SignalR.Publishing;
using Leistd.RealTime.Publishing;
using Leistd.RealTime.Subscriptions;

namespace Leistd.RealTime.AspNetCore.SignalR;

/// <summary>实时组件 SignalR 传输的注册与端点映射。</summary>
public static class DependencyInjection
{
    /// <summary>业务事件 Hub 的默认路径。</summary>
    public const string DefaultRealTimeHubPath = "/hubs/realtime";

    /// <summary>注册 SignalR 实时基础设施：业务事件推送器。</summary>
    /// <remarks>
    /// 内部调用 <c>AddSignalR()</c>。通知组件（Leistd.Notifications.AspNetCore.SignalR）
    /// 可在此基础上叠加自己的 Hub 与发布器。可重复调用：服务只注册一次。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddRealTimeSignalR();
    ///
    /// app.MapRealTimeHub();   // 默认 /hubs/realtime
    /// </code>
    /// </example>
    public static IServiceCollection AddRealTimeSignalR(this IServiceCollection services)
    {
        services.AddRealTime();
        // 走 SignalR 基座而不是裸 AddSignalR：Hub 方法调用不经中间件，主体、租户、链路标识与 UserIdentifier 由基座建立
        services.AddSignalRAmbientContext();

        // 幂等：重复注册会让同一事件推两遍
        services.TryAddSingleton<IBusinessEventPublisher, SignalRBusinessEventPublisher>();

        return services;
    }

    /// <summary>映射实时业务事件 Hub 端点（需登录）。</summary>
    /// <remarks>
    /// <para>握手按 <see cref="HubIdentityOptions.PolicyName"/> 授权，未设置时按宿主的默认策略；Hub 方法调用由 SignalR 基座
    /// 按同一策略复评。要换 Hub 的授权策略就设置该选项，不要在返回的构建器上追加 <c>RequireAuthorization</c>：
    /// 追加的策略只在握手时生效，调用期不复评。</para>
    /// <para>返回官方的 <see cref="HubEndpointConventionBuilder"/>：宿主可继续链式追加 CORS 等端点约定。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddRealTimeSignalR();
    /// builder.Services.Configure&lt;HubIdentityOptions&gt;(options =&gt; options.PolicyName = "Realtime");
    ///
    /// app.MapRealTimeHub();
    /// </code>
    /// </example>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <param name="pattern">Hub 路径，默认 <see cref="DefaultRealTimeHubPath"/>。</param>
    public static HubEndpointConventionBuilder MapRealTimeHub(
        this IEndpointRouteBuilder endpoints,
        string pattern = DefaultRealTimeHubPath)
    {
        // 授权器缺失时映射即失败并指明该注册什么，而不是首次订阅才失败。
        // 只问是否注册、不从根容器解析：授权器通常依赖作用域服务。
        var probe = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        // 框架的 Microsoft DI 容器提供探针；没有探针时沿用组件的组合检查约定跳过，
        // 不为未声明支持的容器从根作用域构造授权器。
        if (probe is not null && !probe.IsService(typeof(IRealTimeSubscriptionAuthorizer)))
        {
            throw new InvalidOperationException(
                $"No {nameof(IRealTimeSubscriptionAuthorizer)} is registered. Every Subscribe call goes " +
                "through it, so the host must decide who may subscribe to which resource. Register your " +
                "own authorizer, or call AddAllowAllRealTimeSubscriptions() to state explicitly that any " +
                "authenticated client may subscribe to any resource key.");
        }

        return endpoints.MapHub<RealTimeHub>(pattern).RequireHubAuthorization();
    }
}
