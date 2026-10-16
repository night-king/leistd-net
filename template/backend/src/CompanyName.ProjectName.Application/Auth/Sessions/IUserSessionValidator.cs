#if (LocalIdentity)
using System.Security.Claims;

namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>会话 Cookie 的服务端校验：Cookie 里的会话仍然有效才放行，并判定它是否受限。</summary>
public interface IUserSessionValidator
{
    /// <summary>
    /// 校验会话主体。会话已撤销、已过期或不属于该用户时无效。
    /// 账号本身是否可用不在此判定，由默认授权策略负责。
    /// </summary>
    /// <remarks>
    /// <para>有效性按 <c>UserSession.TouchInterval</c> 缓存：本实例上的撤销立即生效，
    /// 其他实例在未共享缓存（未配 Redis）时最多滞后一个间隔。</para>
    /// <para>受限判定不进缓存，命中缓存时也照做：每次按主体的租户读取"要求两步验证"，开启时再按主键读本人是否已启用。
    /// 策略与两步验证状态的变更在提交之后的新请求里即可见，不依赖缓存，多实例同样成立。</para>
    /// <para>会话空闲时限按主体所在租户的策略判定过期；只有实际查过会话的那次才在结果里给出该时限，
    /// 供会话 Cookie 收敛票据有效期。</para>
    /// </remarks>
    Task<SessionValidationResult> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}
#endif
