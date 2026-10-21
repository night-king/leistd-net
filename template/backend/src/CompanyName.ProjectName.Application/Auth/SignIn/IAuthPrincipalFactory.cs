#if (LocalIdentity)
using System.Security.Claims;

namespace CompanyName.ProjectName.Application.Auth.SignIn;

/// <summary>对外身份材料装配：签发用的主体，以及 userinfo 端点的 claim 集合。</summary>
/// <remarks>
/// 两个方法都按用户 ID 取用户，把「解析用户」与「此刻是否仍允许访问」收在这一处：
/// 这段判断曾在三个协议端点里各写一份，改一处漏两处不会有任何编译或测试信号。
/// 取不到（或签发场景下不允许访问）时返回 <see langword="null"/>，
/// 回什么由调用方按协议决定——授权与令牌端点回 Forbid，userinfo 回 Challenge。
/// </remarks>
public interface IAuthPrincipalFactory
{
    /// <summary>装配签发用的主体；用户不存在或此刻不允许访问时返回 <see langword="null"/>。</summary>
    Task<ClaimsPrincipal?> CreateAsync(
        Guid userId,
        IEnumerable<string>? scopes = null,
        CancellationToken cancellationToken = default);

    /// <summary>按授权码或刷新令牌的主体重新装配签发用的主体；主体、租户或用户无效时返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 令牌端点的请求不带用户身份，解析链只能得出宿主；用户与租户都取自令牌主体，在其租户内加载用户。
    /// 令牌主体带会话标识（会话绑定客户端签发时写入）时，会话必须仍然有效，否则返回 <see langword="null"/>；
    /// 判定不记活跃，续期不会延长 Identity 会话。
    /// </remarks>
    /// <param name="tokenPrincipal">授权码或刷新令牌的主体。</param>
    /// <param name="scopes">本次签发的 scope。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ClaimsPrincipal?> CreateFromTokenAsync(
        ClaimsPrincipal tokenPrincipal,
        IEnumerable<string>? scopes = null,
        CancellationToken cancellationToken = default);

    /// <summary>装配 userinfo 端点的 claim 集合；主体、租户或用户无效时返回 <see langword="null"/>。</summary>
    /// <param name="tokenPrincipal">本次请求的令牌主体：用户与租户取自它，也决定按哪些 scope 投影、角色取自哪里。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IDictionary<string, object>?> CreateUserInfoAsync(
        ClaimsPrincipal tokenPrincipal,
        CancellationToken cancellationToken = default);
}
#endif
