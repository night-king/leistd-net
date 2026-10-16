#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>本次请求里已通过服务端校验的会话 Cookie 及其受限状态，同一请求内的各个读取方共用这一份。</summary>
/// <remarks>
/// <para>只在会话 Cookie 校验有效之后由宿主的认证事件写入；没有写入（未登录、Bearer 请求、校验无效）时
/// <see cref="SessionId"/> 为 <see langword="null"/>，<see cref="TwoFactorSetupRequired"/> 为 <see langword="false"/>。</para>
/// <para>读取方要的是"这个 Cookie 主体"的状态时（例如在同一请求里另行认证会话 Cookie 的协议端点），
/// 先比对 <see cref="SessionId"/> 与主体的 <c>sid</c>；不一致或缺失时自己调用 <see cref="IUserSessionValidator"/>，
/// 不按"不受限"猜测。</para>
/// </remarks>
public interface ICurrentSessionState
{
    /// <summary>已校验的会话 Id（<c>sid</c>）；本请求没有已校验的会话 Cookie 时为 <see langword="null"/>。</summary>
    Guid? SessionId { get; }

    /// <summary>该会话是否受限（组织要求两步验证而本人尚未启用）。</summary>
    bool TwoFactorSetupRequired { get; }

    /// <summary>记录会话 Cookie 的校验结果。只由认证事件在校验有效之后调用。</summary>
    /// <param name="sessionId">会话 Id。</param>
    /// <param name="twoFactorSetupRequired">是否受限。</param>
    void Set(Guid sessionId, bool twoFactorSetupRequired);
}
#endif
