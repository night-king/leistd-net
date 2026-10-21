#if (IncludeNotifications)
#if (LocalIdentity)
using CompanyName.ProjectName.Api.Notifications;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Notifications.Constants;
#endif
#if (Email)
using CompanyName.ProjectName.Application.Notifications.Provider;
using Leistd.Notifications.Email;
using Leistd.Notifications.Email.Recipients;
#endif
using Leistd.Notifications.AspNetCore.SignalR;
using Leistd.Notifications.Settings;
#if (LocalIdentity)
using Leistd.Notifications.Settings.Options;
#endif
#if (IncludeRealTime)
using Leistd.RealTime.AspNetCore.SignalR.Hubs;
#endif
#if (LocalIdentity || Email)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>通知的注册入口：推送通道、收件偏好、邮件渠道与安全提醒。</summary>
public static class NotificationExtensions
{
    /// <summary>注册通知的投递与本项目的收件人、安全提醒实现。</summary>
    public static IServiceCollection AddMyProjectNotifications(this IServiceCollection services)
    {
#if (IncludeRealTime)
        // 通知与业务事件共用实时 Hub：客户端只建一条连接，通知的接收授权即该 Hub 的授权要求
        services.AddNotificationsSignalR<RealTimeHub>();
#else
        // 通知经通知自己的 Hub 推送（/hubs/notifications）
        services.AddNotificationsSignalR();
#endif

        // 通知偏好（收件人自己的用户级设置）决定哪类通知经哪个渠道收
#if (LocalIdentity)
        // 安全提醒的站内通知必达，不受偏好影响
        services.AddNotificationPreferences(options =>
            options.MandatoryDeliveries.Add(new NotificationDelivery(AppNotificationTypes.Security, AppNotificationChannels.InApp)));
#else
        services.AddNotificationPreferences();
#endif
#if (Email)
        // 邮件渠道只发已验证的邮箱，经后台队列发送
        services.AddEmailNotifications();
        services.TryAddTransient<INotificationRecipientResolver, UserEmailRecipientResolver>();
#endif
#if (LocalIdentity)
        // 安全提醒（新设备登录、密码、邮箱与两步验证变更、账号锁定）经通知组件发给本人：
        // 有意替换应用层默认不发的实现，Replace 与 AddApplicationServices 的先后无关
        services.Replace(ServiceDescriptor.Transient<ISecurityAlertPublisher, NotificationSecurityAlertPublisher>());
#endif

        return services;
    }
}
#endif
