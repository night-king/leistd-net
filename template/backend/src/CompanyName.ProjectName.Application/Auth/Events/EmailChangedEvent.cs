#if (LocalIdentity)
using Leistd.EventBus.Events;

namespace CompanyName.ProjectName.Application.Auth.Events;

/// <summary>账号的邮箱换成了另一个地址（本人修改或管理员修改）。</summary>
/// <remarks>
/// 旧地址与它是否已验证由发布方在改写前从库里读取，不取自请求；处理器只用这份快照，不再按用户读当前地址——
/// 提醒要发给被换掉的那个地址，而此刻账号上已是新地址。有工作单元时提交之后分发，回滚的修改不发提醒。
/// </remarks>
/// <param name="userId">账号本人。</param>
/// <param name="oldEmail">换掉之前的地址。</param>
/// <param name="oldEmailConfirmed">换掉之前的地址是否已验证。</param>
/// <param name="changedBy">执行修改的用户：本人修改时与 <paramref name="userId"/> 相同。</param>
/// <param name="occurredOn">发生时刻，由发布方从 <c>IClock</c> 取得。</param>
public sealed class EmailChangedEvent(
    Guid userId,
    string oldEmail,
    bool oldEmailConfirmed,
    Guid? changedBy,
    DateTime occurredOn) : LocalEvent(occurredOn)
{
    /// <summary>账号本人。</summary>
    public Guid UserId { get; } = userId;

    /// <summary>换掉之前的地址。</summary>
    public string OldEmail { get; } = oldEmail;

    /// <summary>换掉之前的地址是否已验证；未验证的地址（含外部登录建号时的占位地址）不发账号安全邮件。</summary>
    public bool OldEmailConfirmed { get; } = oldEmailConfirmed;

    /// <summary>执行修改的用户：本人修改时与 <see cref="UserId"/> 相同。</summary>
    public Guid? ChangedBy { get; } = changedBy;
}
#endif
