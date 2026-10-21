using CompanyName.ProjectName.Application.Permissions.Provider;

namespace CompanyName.ProjectName.Application.RealTime.Provider;

/// <summary>客户端可订阅的实时资源与事件名。</summary>
/// <remarks>
/// <para>资源键一律经 <c>ICurrentTenant.ScopeKey(资源名)</c> 生成：租户为 <c>{租户 Id:N}:{资源名}</c>，宿主为
/// <c>host:{资源名}</c>。发布与订阅两侧用同一个函数，不同租户、租户与宿主的键天然不同，组名里就带着作用域——
/// 只在授权器里比对租户、却让几个租户共用同一个键，推送仍会发给所有租户的订阅者。</para>
/// <para>新增资源时在这里登记资源名与查看它所需的权限：订阅者必须能看到这份数据本身，推送只是让它及时刷新。</para>
/// </remarks>
public static class AppRealTimeResources
{
    /// <summary>角色列表。</summary>
    public const string Roles = "roles";

    /// <summary>角色列表发生变化（新建、修改、删除）时推送的客户端方法名。</summary>
    public const string RolesChanged = "Roles.Changed";

    /// <summary>各资源要求的查看权限。</summary>
    public static IReadOnlyDictionary<string, string> RequiredPermissions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Roles] = PermissionConstant.Roles.Default
        };
}
