#if (IncludeNotifications)
using Leistd.Notifications.Channels;

namespace CompanyName.ProjectName.Application.Notifications.Constants;

/// <summary>本项目的通知渠道名（渠道的 <c>INotificationChannel.Name</c>）。</summary>
/// <remarks>
/// <para>不是"选一个渠道"：每条通知发布时依次经过<b>所有</b>已注册的渠道，由投递过滤器按收件人的偏好
/// <b>逐个渠道</b>决定投不投，同一条通知可以既进站内、又发邮件。</para>
/// <para>名字要在两处一致：渠道实现的 <c>Name</c>，以及通知偏好的设置名 <c>Notifications.{类别}.{渠道}</c>
/// （见 <c>SettingConstant.Notifications</c>）。</para>
/// </remarks>
public static class AppNotificationChannels
{
    /// <summary>站内：通知历史与实时推送（框架自带的渠道）。</summary>
    public const string InApp = INotificationChannel.InAppName;

#if (Email)
    /// <summary>邮件：只发到收件人已验证的邮箱。</summary>
    public const string Email = "Email";
#endif
}
#endif
