using Microsoft.AspNetCore.SignalR;

namespace Leistd.Notifications.AspNetCore.SignalR.Hubs;

/// <summary>通知 Hub：客户端在此接收自己的通知。</summary>
/// <remarks>
/// 没有可供客户端调用的方法，也不做分组：按用户寻址用 SignalR 自带的
/// <c>Clients.User(userId)</c>，它匹配的 <c>UserIdentifier</c> 由 SignalR 基座的
/// <c>ClaimsSignalRUserIdProvider</c> 按配置的 claim 顺序解析。
/// </remarks>
public class NotificationHub : Hub;
