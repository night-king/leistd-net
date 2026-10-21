#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using Leistd.EventBus.Events;

namespace CompanyName.ProjectName.Application.Auth.Events;

/// <summary>某个用例要求给账号本人发一条安全提醒（改密、管理员重置、两步验证变更）。</summary>
/// <remarks>
/// 由应用服务按用例发布：本人修改与管理员重置是同一次实体变更的两种用例语义，实体分不出来。
/// 有工作单元时提交之后分发，回滚的变更不发提醒。
/// </remarks>
/// <param name="userId">账号本人。</param>
/// <param name="alert">提醒内容。</param>
/// <param name="occurredOn">发生时刻，由发布方从 <c>IClock</c> 取得。</param>
public sealed class SecurityAlertRequestedEvent(Guid userId, SecurityAlert alert, DateTime occurredOn)
    : LocalEvent(occurredOn)
{
    /// <summary>账号本人。</summary>
    public Guid UserId { get; } = userId;

    /// <summary>提醒内容。</summary>
    public SecurityAlert Alert { get; } = alert;
}
#endif
