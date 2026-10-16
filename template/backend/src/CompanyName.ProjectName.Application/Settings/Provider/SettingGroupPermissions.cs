using CompanyName.ProjectName.Application.Permissions.Provider;

namespace CompanyName.ProjectName.Application.Settings.Provider;

/// <summary>系统设置分组 → 管理该分组所需的子权限。</summary>
/// <remarks>
/// <para>设置端点据此给分组登记授权策略，操作记录据此写"凭什么"；新增系统设置分组时在这里加一行，并补子权限定义与译文。</para>
/// <para>个人偏好分组（显示、通知）不在其中：它们只允许用户层，不出现在系统设置里。</para>
/// </remarks>
public static class SettingGroupPermissions
{
    /// <summary>全部对应关系。</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
#if (LocalIdentity)
        [SettingConstant.Groups.Registration] = PermissionConstant.Settings.Registration,
        [SettingConstant.Groups.Security] = PermissionConstant.Settings.Security,
#if (ExternalLogin)
        [SettingConstant.Groups.ExternalLogin] = PermissionConstant.Settings.ExternalLogin,
#endif
#if (Email)
        [SettingConstant.Groups.Email] = PermissionConstant.Settings.Email,
#endif
#endif
        [SettingConstant.Groups.Operations] = PermissionConstant.Settings.Operations,
#if (IncludeOperationRecords || IncludeNotifications)
        [SettingConstant.Groups.Retention] = PermissionConstant.Settings.Retention,
#endif
    };
}
