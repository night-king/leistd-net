using Leistd.Authorization.Checking;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Extensions;
using Leistd.RealTime.Subscriptions;

namespace CompanyName.ProjectName.Application.RealTime.Provider;

/// <summary>实时资源的订阅授权：资源键必须属于当前作用域，且订阅者持有查看该资源的权限。</summary>
/// <remarks>
/// Hub 调用内的租户与主体由 SignalR 基座按连接建立。键与当前作用域逐字比对，因此跨租户、
/// 宿主与租户互订、未登记的资源一律拒绝；权限按订阅那一刻判定，撤权后的下一次订阅即被拒。
/// </remarks>
public sealed class AppRealTimeSubscriptionAuthorizer(
    ICurrentTenant currentTenant,
    IPermissionChecker permissionChecker) : IRealTimeSubscriptionAuthorizer
{
    public async Task<bool> AuthorizeAsync(
        RealTimeSubscriptionContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var (resource, permission) in AppRealTimeResources.RequiredPermissions)
        {
            if (string.Equals(context.ResourceKey, currentTenant.ScopeKey(resource), StringComparison.Ordinal))
            {
                return await permissionChecker.IsGrantedAsync(permission, cancellationToken);
            }
        }

        return false;
    }
}
