namespace Leistd.Notifications.AspNetCore.SignalR;

/// <summary>通知推送到客户端时调用的方法名。</summary>
/// <remarks>
/// 通知可与业务实时事件共用一个 Hub（见 <c>AddNotificationsSignalR&lt;THub&gt;()</c>），业务事件不要使用 <c>Notifications.</c> 前缀。
/// </remarks>
public static class NotificationClientMethods
{
    /// <summary>收到一条通知，参数是 <c>NotificationOutputDto</c>。</summary>
    public const string Received = "Notifications.Received";
}
