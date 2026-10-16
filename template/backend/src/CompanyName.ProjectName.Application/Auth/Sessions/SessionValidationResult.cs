#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>会话 Cookie 的服务端校验结果。</summary>
/// <remarks>有效与受限是两件事：受限的会话仍然有效，只是服务端只放行完成两步验证设置所需的入口，不能当成无效而退出登录。</remarks>
/// <param name="IsValid">会话仍然有效。</param>
/// <param name="TwoFactorSetupRequired">
/// 受限会话：本人所在租户（或宿主）要求两步验证而本人尚未启用。只在 <paramref name="IsValid"/> 为真时有意义。
/// </param>
/// <param name="IdleTimeout">
/// 这次按主体所在租户读到的会话空闲时限；只在实际查过会话时给出（缓存命中、租户不存在或已停用时为 <see langword="null"/>）。
/// 会话 Cookie 据此把票据有效期收敛到当前策略。
/// </param>
public sealed record SessionValidationResult(bool IsValid, bool TwoFactorSetupRequired, TimeSpan? IdleTimeout = null)
{
    /// <summary>会话无效（已撤销、已过期或不属于该用户）。</summary>
    public static SessionValidationResult Invalid { get; } = new(false, false);

    /// <summary>会话有效，按 <paramref name="twoFactorSetupRequired"/> 决定是否受限。</summary>
    /// <param name="twoFactorSetupRequired">是否受限。</param>
    /// <param name="idleTimeout">实际查过会话时读到的空闲时限；未查时为 <see langword="null"/>。</param>
    public static SessionValidationResult Valid(bool twoFactorSetupRequired, TimeSpan? idleTimeout = null)
        => new(true, twoFactorSetupRequired, idleTimeout);
}
#endif
