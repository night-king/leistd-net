#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using Leistd.EventBus.EventHandlers;

namespace CompanyName.ProjectName.Application.Auth.EventHandlers;

/// <summary>用例提交之后发出安全提醒：告诉本人的是已经生效的变更，回滚的不发。</summary>
/// <remarks>投递失败由 <see cref="ISecurityAlertPublisher"/> 的实现自己吞掉，不让已提交的用例报错。</remarks>
internal sealed class SecurityAlertRequestedEventHandler(ISecurityAlertPublisher securityAlerts)
    : IEventHandler<SecurityAlertRequestedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(SecurityAlertRequestedEvent @event, CancellationToken cancellationToken = default) =>
        securityAlerts.PublishAsync(@event.UserId, @event.Alert, cancellationToken);
}
#endif
