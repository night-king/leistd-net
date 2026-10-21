using Leistd.Data.Querying;
using Leistd.Ddd.Domain.Entities;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Leistd.Ddd.Domain.Repositories;

/// <summary>标记可按约定注册的仓储。</summary>
/// <remarks>业务代码应实现或注入泛型仓储。</remarks>
public interface IRepository
{
}

/// <summary>聚合根仓储：读写单一实体类型。</summary>
/// <remarks>
/// <para>写方法是否立即落库取决于当前是否处在工作单元内：在工作单元内只登记变更，由工作单元统一提交；
/// 不在工作单元内则每次调用各自保存。</para>
/// <para>读方法一律经全局查询过滤器（软删除、租户隔离）；需要绕过时用
/// <c>IDataFilter.Disable&lt;T&gt;()</c> 在作用域内显式关闭。</para>
/// </remarks>
/// <typeparam name="TEntity">实体类型。</typeparam>
public interface IRepository<TEntity> : IRepository where TEntity : class, IEntity
{
    /// <summary>取本实体的可组合查询，用于仓储方法覆盖不到的复杂查询。</summary>
    /// <remarks>
    /// 返回的 <see cref="IQueryable{T}"/> 已套用全局过滤器，并绑定当前工作单元的上下文实例；
    /// EF 仓储的所有读取经此入口；覆写须保留原实体身份和跟踪，不得投影或禁用跟踪。
    /// 异步执行请用 <see cref="IQueryableAsyncExecuter"/>，不要直接调 EF Core 的扩展方法。
    /// </remarks>
    Task<IQueryable<TEntity>> GetQueryableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件取唯一一条；无匹配返回 <see langword="null"/>，匹配多于一条抛
    /// <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <remarks>用于条件本身保证唯一的场景；条件可能命中多条时用 <see cref="GetFirstAsync"/>。</remarks>
    Task<TEntity?> GetOneAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>按条件取第一条；无匹配返回 <see langword="null"/>。</summary>
    /// <param name="predicate">筛选条件。</param>
    /// <param name="orderBy">排序；不传时由数据库决定顺序，结果不稳定。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TEntity?> GetFirstAsync(Expression<Func<TEntity, bool>> predicate, Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null, CancellationToken cancellationToken = default);

    /// <summary>取满足条件的全部实体；<paramref name="predicate"/> 为 <see langword="null"/> 时取全部。</summary>
    /// <remarks>无分页与上限，面向已知规模的集合；不确定规模时用 <see cref="GetQueryableAsync"/> 自行分页。</remarks>
    Task<IEnumerable<TEntity>> GetListAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken cancellationToken = default);

    /// <summary>统计满足条件的条数；<paramref name="predicate"/> 为 <see langword="null"/> 时统计全部。</summary>
    Task<long> CountAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken cancellationToken = default);

    /// <summary>判断是否存在满足条件的实体；<paramref name="predicate"/> 为 <see langword="null"/> 时判断是否非空。</summary>
    Task<bool> AnyAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken cancellationToken = default);

    /// <summary>新增一个实体，返回登记后的实例（数据库生成的值在提交后才可用）。</summary>
    /// <remarks>
    /// 工作单元内只登记到变更跟踪器、不访问数据库：唯一索引、外键与检查约束冲突在冲刷时才抛出
    /// （<c>IUnitOfWork.SaveChangesAsync</c>，或提交时的 <c>CompleteAsync</c>），需要就地捕获时先显式冲刷。
    /// 工作单元外立即保存。
    /// </remarks>
    Task<TEntity> InsertAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>批量新增。</summary>
    Task InsertManyAsync([NotNull] IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

    /// <summary>更新一个实体，返回登记后的实例。</summary>
    Task<TEntity> UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>批量更新。</summary>
    Task UpdateManyAsync([NotNull] IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

    /// <summary>删除一个实体；实体实现 <c>ISoftDelete</c> 时为逻辑删除。</summary>
    Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>批量删除给定实体；实体实现 <c>ISoftDelete</c> 时为逻辑删除。</summary>
    Task DeleteManyAsync([NotNull] IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

    /// <summary>按条件删除。</summary>
    /// <remarks>使用原始实体集合，不受自定义读筛选影响；全局过滤器仍生效。先加载匹配集再删除，审计与软删除照常生效。</remarks>
    Task DeleteManyAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default);
}

/// <summary>带主键的聚合根仓储。</summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
/// <typeparam name="TKey">主键类型。</typeparam>
public interface IRepository<TEntity, TKey> : IRepository<TEntity> where TEntity : class, IEntity<TKey>
{
    /// <summary>按主键取实体；不存在或被全局过滤器排除时返回 <see langword="null"/>。</summary>
    /// <remarks>走 LINQ 查询而非 <c>FindAsync</c>，因此软删除与租户隔离过滤器照常生效。</remarks>
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>按主键删除；实体不存在时静默返回。实体实现 <c>ISoftDelete</c> 时为逻辑删除。</summary>
    /// <remarks>先经读入口查找，受自定义读筛选及全局过滤器影响。</remarks>
    Task DeleteAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>按主键批量删除；不存在的主键被忽略。实体实现 <c>ISoftDelete</c> 时为逻辑删除。</summary>
    /// <remarks>使用原始实体集合，不受自定义读筛选影响；全局过滤器仍生效。</remarks>
    Task DeleteManyAsync([NotNull] IEnumerable<TKey> ids, CancellationToken cancellationToken = default);
}
