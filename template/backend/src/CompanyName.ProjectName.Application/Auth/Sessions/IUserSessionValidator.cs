#if (LocalIdentity)
using System.Security.Claims;

namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>会话 Cookie 的服务端校验：Cookie 里的会话仍然有效才放行。</summary>
public interface IUserSessionValidator
{
    /// <summary>
    /// 校验会话主体。会话已撤销、已过期或不属于该用户时无效。
    /// 账号本身是否可用不在此判定，由默认授权策略负责。
    /// </summary>
    /// <remarks>
    /// 结果按 <c>UserSession.TouchInterval</c> 缓存：本实例上的撤销立即生效，
    /// 其他实例在未共享缓存（未配 Redis）时最多滞后一个间隔。
    /// </remarks>
    Task<UserSessionValidation> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

/// <summary>一次会话校验的结果。</summary>
/// <param name="IsValid">会话是否仍有效。</param>
/// <param name="IdleTimeout">
/// 这次按主体所在租户读到的会话空闲时限；只在实际查过会话时给出（缓存命中、租户不存在或已停用时为 <see langword="null"/>）。
/// 会话 Cookie 据此把票据有效期收敛到当前策略。
/// </param>
public sealed record UserSessionValidation(bool IsValid, TimeSpan? IdleTimeout = null)
{
    /// <summary>无效。</summary>
    public static UserSessionValidation Invalid { get; } = new(false);

    /// <summary>有效，但这次没有读策略。</summary>
    public static UserSessionValidation Valid { get; } = new(true);
}
#endif
