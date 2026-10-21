using Leistd.Notifications.Filters;
using Leistd.Notifications.Settings.Filters;
using Leistd.Notifications.Settings.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.Notifications.Settings;

/// <summary>通知偏好的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>用收件人的用户级设置决定每类通知经每个渠道是否投递，替换默认的"一律投递"。</summary>
    /// <remarks>
    /// <para>偏好设置由宿主定义：为需要可关闭的组合定义用户级布尔设置，名为 <c>{前缀}.{通知类型}.{渠道名}</c>；
    /// 没有定义的组合一律投递。必达组合经 <see cref="NotificationPreferenceOptions.MandatoryDeliveries"/> 给出。</para>
    /// <para>与通知组件的注册顺序无关，总是成为唯一的投递过滤器。可重复调用：过滤器始终只有一条，
    /// <paramref name="configure"/> 每次都叠加。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddNotificationPreferences(options =&gt;
    ///     options.MandatoryDeliveries.Add(new NotificationDelivery("Security", INotificationChannel.InAppName)));
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">偏好配置。</param>
    public static IServiceCollection AddNotificationPreferences(
        this IServiceCollection services,
        Action<NotificationPreferenceOptions>? configure = null)
    {
        services.AddOptions<NotificationPreferenceOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // 取代 Core 的默认与此前登记的任何过滤器，成为唯一的投递过滤器；先清空再登记，重复调用结果不变
        services.RemoveAll<INotificationDeliveryFilter>();
        services.AddScoped<INotificationDeliveryFilter, SettingsNotificationDeliveryFilter>();
        return services;
    }
}
