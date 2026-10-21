#if (IncludeNotifications)
using Leistd.Notifications.Dtos;

namespace CompanyName.ProjectName.Application.Notifications.Constants;

/// <summary>本项目发布的通知类别（通知的 <c>Type</c>）。</summary>
/// <remarks>
/// 类别由业务项目定义，框架只约定未指定时的默认类型。通知偏好按"类别 × 渠道"存为用户设置，
/// 设置名由类别与渠道拼出（见 <c>SettingConstant.Notifications</c>）；前端按类别选铃铛里的图标。
/// </remarks>
public static class AppNotificationTypes
{
    /// <summary>系统通知：发布时没指定类型的通知都归这一类（框架的默认类型）。</summary>
    public const string System = NotificationInputDto.DefaultType;

#if (LocalIdentity)
    /// <summary>安全提醒：新设备登录、密码与两步验证变更、账号被锁定。站内通知不可关闭。</summary>
    public const string Security = "Security";
#endif
}
#endif
