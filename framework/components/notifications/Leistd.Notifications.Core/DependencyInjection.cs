using Leistd.ExceptionHandling.Options;
using Leistd.Notifications.ExceptionMappings;
using Leistd.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Notifications.Publishing;
using Leistd.Notifications.Errors;
using Leistd.Notifications.Filters;

namespace Leistd.Notifications;

/// <summary>通知核心服务注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册通知发布核心能力。</summary>
    /// <remarks>
    /// 还需要一个 <c>INotificationStore</c> 实现（如 <c>AddNotificationsEfCore&lt;TDbContext&gt;()</c>），
    /// 缺失时解析 <c>INotificationPublisher</c> 失败。可重复调用，结果与调用一次相同。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddNotifications();
    /// // 宿主还须注册一个 INotificationStore 实现（见通知家族文档），否则解析发布器即失败
    ///
    /// // 实时投递是可选关注点，单独注册
    /// await notificationPublisher.PublishToUserAsync(userId, new NotificationInputDto
    /// {
    ///     Title = "审批通过", Type = "Approval", // 类别由业务项目自己定义
    /// }, ct);
    /// </code>
    /// </example>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        // 幂等：通知的持久化与实时包都会调到这里
        services.TryAddTransient<INotificationPublisher, NotificationPublisher>();
        // 默认一律投递；宿主有通知偏好时先注册自己的过滤器（或之后 Replace）
        services.TryAddSingleton<INotificationDeliveryFilter, DeliverAllNotificationFilter>();
        services.AddJsonLocalizationResources(typeof(NotificationErrorCodes).Assembly);
        // 错误码的状态语义与默认译文属于本组件默认值，在此登记，避免宿主漏配时静默回落成 400。
        // 宿主的 MapCode / MapException 覆盖同一码或同一类型，与调用顺序无关。
        services.Configure<GlobalExceptionOptions>(NotificationExceptionMappings.Configure);
        return services;
    }
}
