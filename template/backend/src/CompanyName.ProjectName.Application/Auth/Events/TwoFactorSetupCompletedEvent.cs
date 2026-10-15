#if (LocalIdentity)
using Leistd.EventBus.Events;

namespace CompanyName.ProjectName.Application.Auth.Events;

/// <summary>两步验证设置已确认（首次启用或换绑），待确认的设置密钥不再需要。</summary>
/// <remarks>有工作单元时提交之后分发：启用回滚时设置密钥仍在，本人可以用同一个验证器再确认一次。</remarks>
/// <param name="userId">完成设置的用户。</param>
/// <param name="occurredOn">发生时刻，由发布方从 <c>IClock</c> 取得。</param>
public sealed class TwoFactorSetupCompletedEvent(Guid userId, DateTime occurredOn) : LocalEvent(occurredOn)
{
    /// <summary>完成设置的用户。</summary>
    public Guid UserId { get; } = userId;
}
#endif
