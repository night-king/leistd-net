namespace Leistd.EventBus.Abstractions;

/// <summary>将事件发布给当前进程内的处理器；有活动完成边界（如工作单元）时推迟到边界完成。</summary>
/// <example>
/// <code>
/// public sealed class OrderPlaced : BaseEvent { public Guid OrderId { get; init; } }
///
/// public class OrderPlacedNotifier : IEventHandler&lt;OrderPlaced&gt;
/// {
///     public Task HandleAsync(OrderPlaced e, CancellationToken ct) =&gt; Task.CompletedTask;
/// }
///
/// await localEventBus.PublishAsync(new OrderPlaced { OrderId = orderId }, ct);
/// </code>
/// </example>
public interface ILocalEventBus : IEventBus
{
}
