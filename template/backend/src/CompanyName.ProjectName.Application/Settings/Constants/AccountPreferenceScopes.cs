#if (OpenIddictServer)
namespace CompanyName.ProjectName.Application.Settings.Constants;

/// <summary>资源服务读取账户偏好用的机器 scope。</summary>
/// <remarks>给收件人产出文本时没有用户令牌，资源服务以自己的机器身份按用户读取；只能读，写入走用户委托。</remarks>
public static class AccountPreferenceScopes
{
    public const string Read = "account-preferences.read";
}
#endif
