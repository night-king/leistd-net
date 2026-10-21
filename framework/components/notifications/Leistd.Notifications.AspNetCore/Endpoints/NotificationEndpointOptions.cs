namespace Leistd.Notifications.AspNetCore.Endpoints;

/// <summary>通知中心端点的授权口径。</summary>
/// <remarks>策略名必填，组件不内置默认策略，也不在路由组上叠加宿主默认策略；漏配时 <c>MapNotifications</c> 映射即抛出。</remarks>
public sealed class NotificationEndpointOptions
{
    /// <summary>访问通知中心所需的授权策略名，通常是“已登录的交互用户”。</summary>
    public string AccessPolicy { get; set; } = string.Empty;

    // 缺必填项抛 ArgumentException，ParamName 即属性名
    internal void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(AccessPolicy);
}
