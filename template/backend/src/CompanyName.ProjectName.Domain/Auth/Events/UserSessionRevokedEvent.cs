#if (LocalIdentity)
using Leistd.EventBus.Events;

namespace CompanyName.ProjectName.Domain.Auth.Events;

/// <summary>登录会话被撤销（退出登录、退出其他设备、改密码或重置后作废）。</summary>
/// <remarks>事务提交后发布：会话确实删掉了，订阅方（如会话校验缓存）才跟着作废。</remarks>
/// <param name="sessionId">被撤销的会话。</param>
/// <param name="occurredOn">撤销时刻。</param>
public sealed class UserSessionRevokedEvent(Guid sessionId, DateTime occurredOn) : LocalEvent(occurredOn)
{
    /// <summary>被撤销的会话。</summary>
    public Guid SessionId { get; } = sessionId;
}
#endif
