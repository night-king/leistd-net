namespace Leistd.Ddd.Domain.Entities;

/// <summary>标记作为一致性边界入口的聚合根。</summary>
/// <remarks>
/// 默认仓储只登记实现本标记的实体（见 <c>DddDbContextOptions.AddDefaultRepositories</c>）；
/// <c>IRepository&lt;TEntity&gt;</c> 本身约束的是 <c>IEntity</c>，点名登记与自定义仓储不受本标记限制。
/// </remarks>
public interface IAggregateRoot : IEntity
{
}

/// <summary>标记具有强类型主键的聚合根。</summary>
/// <typeparam name="TKey">主键类型。</typeparam>
public interface IAggregateRoot<TKey> : IAggregateRoot, IEntity<TKey>
{
}
