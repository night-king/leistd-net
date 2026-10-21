using Leistd.Ddd.Domain.Entities;
using Leistd.EventBus.Events;
using Xunit;

namespace Leistd.Ddd.Domain.Tests;

/// <summary>实体基类：主键暴露与领域事件的登记、取出、清空。</summary>
public class EntityTests
{
    private sealed record OrderPlaced(Guid OrderId) : ILocalEvent
    {
        public Guid EventId { get; } = Guid.CreateVersion7();
        public DateTime OccurredOn { get; } = DateTime.UtcNow;
    }

    private sealed class Order : Entity<Guid>
    {
        public Order() { }
        public Order(Guid id) : base(id) { }

        public void Place() => AddLocalEvent(new OrderPlaced(Id));
    }

    private sealed class OrderLine : Entity
    {
        public Guid OrderId { get; init; }
        public int Number { get; init; }

        public override object?[] GetKeys() => [OrderId, Number];
    }

    [Fact]
    public void Single_key_entity_exposes_its_id_as_the_only_key()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal([id], new Order(id).GetKeys());
    }

    // 复合主键按声明顺序返回：顺序错了，基础设施按位置解析就会取错列。
    [Fact]
    public void Composite_key_entity_returns_keys_in_declaration_order()
    {
        var orderId = Guid.CreateVersion7();

        Assert.Equal([orderId, 7], new OrderLine { OrderId = orderId, Number = 7 }.GetKeys());
    }

    // 无参构造供 ORM 物化：主键保持默认值，不能在这里生成一个新的。
    [Fact]
    public void Parameterless_constructor_leaves_the_key_at_its_default()
    {
        Assert.Equal(Guid.Empty, new Order().Id);
    }

    [Fact]
    public void Newly_created_entity_has_no_pending_events()
    {
        Assert.Empty(new Order(Guid.CreateVersion7()).GetLocalEvents());
    }

    [Fact]
    public void Registered_events_are_returned_in_order()
    {
        var order = new Order(Guid.CreateVersion7());

        order.Place();
        order.Place();

        Assert.Equal(2, order.GetLocalEvents().Count);
        Assert.All(order.GetLocalEvents(), e => Assert.IsType<OrderPlaced>(e));
    }

    // 取出的集合是只读视图：基础设施拿到它之后不应该能改实体的待发布队列。
    [Fact]
    public void Returned_event_collection_is_read_only()
    {
        var order = new Order(Guid.CreateVersion7());
        order.Place();

        Assert.IsNotType<List<ILocalEvent>>(order.GetLocalEvents());
    }

    // 收集后必须能清空，否则同一个实体在下一个保存周期会重复发布同一批事件。
    [Fact]
    public void Clearing_removes_all_pending_events()
    {
        var order = new Order(Guid.CreateVersion7());
        order.Place();

        order.ClearLocalEvents();

        Assert.Empty(order.GetLocalEvents());
    }

    [Fact]
    public void ToString_shows_the_type_and_key()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal($"[ENTITY: Order] Id = {id}", new Order(id).ToString());
        Assert.StartsWith("[ENTITY: OrderLine] Keys = ", new OrderLine { Number = 1 }.ToString());
    }

    [Fact]
    public void Concurrency_stamps_are_unique_and_url_safe()
    {
        var stamps = Enumerable.Range(0, 100).Select(_ => ConcurrencyStamps.New()).ToArray();

        Assert.Equal(100, stamps.Distinct().Count());
        Assert.All(stamps, s => Assert.Equal(32, s.Length));
        Assert.All(stamps, s => Assert.DoesNotContain('-', s));
    }
}
