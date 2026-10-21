using Leistd.EventBus.Events;

namespace CompanyName.ProjectName.Application.Roles.Events;

/// <summary>角色列表已变化。资源键在发布时按当时的作用域定案，分发时（可能已在提交之后）不再依赖上下文。</summary>
/// <param name="resourceKey">变化所在作用域的角色列表资源键。</param>
/// <param name="occurredOn">发生时刻，由发布方从 <c>IClock</c> 取得，与业务时间同源。</param>
public sealed class RoleListChangedEvent(string resourceKey, DateTime occurredOn) : LocalEvent(occurredOn)
{
    /// <summary>变化所在作用域的角色列表资源键。</summary>
    public string ResourceKey { get; } = resourceKey;
}
