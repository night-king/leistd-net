using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.AspNetCore.SignalR;
using Leistd.Notifications.AspNetCore.SignalR.Hubs;
using Leistd.Notifications.AspNetCore.SignalR.Channels;
using Leistd.Notifications.Channels;

namespace Leistd.Notifications.AspNetCore.SignalR;

/// <summary>通知 SignalR 传输的注册与端点映射。</summary>
public static class DependencyInjection
{
    /// <summary>通知 Hub 路径。</summary>
    public const string DefaultNotificationHubPath = "/hubs/notifications";

    /// <summary>注册基于 SignalR 的通知发布器，经通知自己的 <see cref="NotificationHub"/> 推送。</summary>
    /// <remarks>
    /// 等价于 <c>AddNotificationsSignalR&lt;NotificationHub&gt;()</c>，配合 <see cref="MapNotificationHub"/> 使用。
    /// 内部只注册通知传输所需的 SignalR 能力，不注册实时业务 Hub、在线状态或业务事件发布器。
    /// 通知持久化请另行调用通知 EF Core 包提供的 AddNotificationsEfCore 泛型方法。
    /// 重复调用的规则同泛型重载：重复调用幂等，已为通知选定其他 Hub 时抛出 <see cref="InvalidOperationException"/>。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddNotificationsSignalR();
    ///
    /// app.MapNotificationHub();   // 默认 /hubs/notifications，要求登录
    /// </code>
    /// </example>
    public static IServiceCollection AddNotificationsSignalR(this IServiceCollection services)
        => services.AddNotificationsSignalR<NotificationHub>();

    /// <summary>注册基于 SignalR 的通知发布器，经宿主指定的 Hub 推送。</summary>
    /// <remarks>
    /// <para>用于让通知与业务实时事件共用一个 Hub、一条客户端连接：通知按用户寻址推送到 <typeparamref name="THub"/>，
    /// 客户端方法名为 <see cref="NotificationClientMethods.Received"/>。该 Hub 由宿主自行映射，
    /// 此时不再调用 <see cref="MapNotificationHub"/>；通知的接收授权随之就是该 Hub 的授权要求。</para>
    /// <para>同一个 Hub 重复调用是幂等的；已选定一个 Hub 后再指定另一个（包括先调用无泛型版本）会抛
    /// <see cref="InvalidOperationException"/>。</para>
    /// </remarks>
    /// <typeparam name="THub">推送通知所经的 Hub。</typeparam>
    /// <exception cref="InvalidOperationException">已为通知选定了另一个 Hub。</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddRealTimeSignalR();
    /// builder.Services.AddNotificationsSignalR&lt;RealTimeHub&gt;();
    ///
    /// app.MapRealTimeHub();   // 通知与业务事件共用这一条连接
    /// </code>
    /// </example>
    public static IServiceCollection AddNotificationsSignalR<THub>(this IServiceCollection services)
        where THub : Hub
    {
        var selected = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(NotificationHubClients));
        if (selected is not null)
        {
            if (selected.ImplementationType != typeof(NotificationHubClients<THub>))
            {
                var current = selected.ImplementationType?.GetGenericArguments().SingleOrDefault()?.FullName;
                throw new InvalidOperationException(
                    $"Notifications are already pushed through hub '{current}'; cannot also push them through " +
                    $"'{typeof(THub).FullName}'. Call AddNotificationsSignalR with a single hub.");
            }

            return services;
        }

        services.AddNotifications();
        // 走 SignalR 基座而不是裸 AddSignalR：Hub 方法调用不经中间件，主体、租户、链路标识与 UserIdentifier 由基座建立。
        // 基座注册幂等，与 realtime 组件同时装也只有一份过滤器。
        services.AddSignalRAmbientContext();
        services.AddSingleton<NotificationHubClients, NotificationHubClients<THub>>();
        // 渠道是累加扩展点，按实现类型去重，保留宿主注册的其他渠道。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<INotificationChannel, SignalRNotificationChannel>());
        return services;
    }

    /// <summary>映射通知 Hub 端点（需登录）。</summary>
    /// <remarks>
    /// 只映射通知自身的 Hub，实时业务事件 Hub 由实时组件的 <c>MapRealTimeHub()</c> 映射。
    /// 握手按 <c>HubIdentityOptions.PolicyName</c> 授权（未设置时按宿主的默认策略），Hub 方法调用由 SignalR 基座按同一策略复评；
    /// 要换策略就设置该选项，不要在返回的构建器上追加 <c>RequireAuthorization</c>。
    /// 返回官方的 <see cref="HubEndpointConventionBuilder"/>，宿主可继续链式追加 CORS 等端点约定。
    /// </remarks>
    public static HubEndpointConventionBuilder MapNotificationHub(
        this IEndpointRouteBuilder endpoints,
        string notificationHubPath = DefaultNotificationHubPath)
    {
        return endpoints.MapHub<NotificationHub>(notificationHubPath).RequireHubAuthorization();
    }
}
