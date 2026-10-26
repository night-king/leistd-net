#if (OpenIddictServer)
namespace CompanyName.ProjectName.Application.Settings.Constants;

/// <summary>账户偏好机器端点的授权策略名，与 <see cref="AccountPreferenceScopes"/> 成对。</summary>
public static class AccountPreferencePolicies
{
    /// <summary>按用户读取账户偏好（资源服务回源用）。</summary>
    public const string Read = "AccountPreferences.Read";
}
#endif
