using Leistd.EventBus.EventHandlers;
using CompanyName.ProjectName.Application.RealTime.Provider;
using CompanyName.ProjectName.Application.Roles.Events;
using Leistd.RealTime.Publishing;

namespace CompanyName.ProjectName.Application.Roles.EventHandlers;

/// <summary>角色列表变化后，推送给订阅了同一作用域角色列表的客户端。</summary>
/// <remarks>
/// 有工作单元时事件在提交之后分发，回滚的写入不推送；没有工作单元的单次写入在仓储保存之后发布。
/// 两种情况下客户端收到提示时，变更都已持久化。
/// 推送只是提示"该刷新了"，不携带数据——列表内容仍经受权限保护的查询接口获取。
/// </remarks>
public sealed class RoleListChangedEventHandler(IBusinessEventPublisher publisher)
    : IEventHandler<RoleListChangedEvent>
{
    public Task HandleAsync(RoleListChangedEvent @event, CancellationToken cancellationToken = default)
        => publisher.PublishToResourceAsync(@event.ResourceKey, AppRealTimeResources.RolesChanged, new { }, cancellationToken);
}
