#if (OpenIddictServer || RemoteTokenAuth)
namespace CompanyName.ProjectName.Api.Auth.Sessions;

/// <summary>浏览器会话（BFF）续期访问令牌的时机：剩余寿命不超过 <see cref="Lead"/> 时，下一次请求先换新令牌。</summary>
/// <remarks>
/// 资源服务按它判断何时刷新；签发方据它限定访问令牌寿命的支持范围——寿命不长于它时，
/// 刚签发的令牌就已落在刷新窗口里，会话的每个请求都要去刷新。
/// </remarks>
internal static class AccessTokenRenewal
{
    /// <summary>提前刷新的窗口。</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(1);
}
#endif
