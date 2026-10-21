namespace Leistd.Notifications.Errors;

/// <summary>通知组件发出的错误码，兼本地化资源键；默认中英文案随包分发。</summary>
public static class NotificationErrorCodes
{
    /// <summary>当前身份不是用户（如机器客户端），不能读写用户通知。</summary>
    public const string IdentityCannotOperate = "Notification:IdentityCannotOperate";
}
