using Leistd.EventBus.Events;

namespace Leistd.Ddd.Domain.Entities;

/// <summary>标记领域实体并公开其主键值。</summary>
public interface IEntity
{
    /// <summary>返回构成本实体主键的值；复合主键按声明顺序返回多个。</summary>
    object?[] GetKeys();
}

/// <summary>带单一主键的实体。</summary>
/// <typeparam name="TKey">主键类型。</typeparam>
public interface IEntity<TKey> : IEntity
{
    /// <summary>实体主键。</summary>
    TKey Id { get; }
}

/// <summary>实体基类：提供领域事件的登记与取出。</summary>
/// <remarks>
/// 登记的事件由 <c>BaseDbContext</c> 在保存时收集并按工作单元阶段发布；
/// 脱离 Infrastructure 层（如纯领域单测直接 <c>new</c> 实体）不会有人发布它们。
/// </remarks>
public abstract class Entity : IEntity
{
    private readonly List<ILocalEvent> _localEvents = new();

    /// <inheritdoc />
    public abstract object?[] GetKeys();

    /// <summary>登记一个领域事件，在本实体所在的保存周期发布。</summary>
    protected void AddLocalEvent(ILocalEvent @event)
    {
        _localEvents.Add(@event);
    }

    /// <summary>取出待发布的领域事件。</summary>
    /// <remarks>供基础设施层收集事件用，业务代码不应调用。</remarks>
    public IReadOnlyCollection<ILocalEvent> GetLocalEvents() => _localEvents.AsReadOnly();

    /// <summary>清空已登记的领域事件。</summary>
    /// <remarks>供基础设施层在收集后调用，业务代码不应调用。</remarks>
    public void ClearLocalEvents() => _localEvents.Clear();

    /// <inheritdoc />
    public override string ToString()
    {
        return $"[ENTITY: {GetType().Name}] Keys = {string.Join(", ", GetKeys())}";
    }
}

/// <inheritdoc cref="IEntity{TKey}" />
public abstract class Entity<TKey> : Entity, IEntity<TKey>
{
    /// <summary>实体主键，由构造函数或持久化层赋值。</summary>
    public virtual TKey Id { get; protected set; } = default!;

    /// <summary>供 ORM 物化使用的无参构造。</summary>
    protected Entity() { }

    /// <summary>以指定主键构造实体。</summary>
    /// <param name="id">实体主键。</param>
    protected Entity(TKey id)
    {
        Id = id;
    }

    /// <inheritdoc />
    public override object?[] GetKeys() => [Id];

    /// <inheritdoc />
    public override string ToString()
    {
        return $"[ENTITY: {GetType().Name}] Id = {Id}";
    }
}
