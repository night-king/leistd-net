using System.Linq.Expressions;
using Leistd.Authorization.Subjects;

namespace Leistd.Authorization.DataScope.Abstractions;

/// <summary>常用数据操作名称；业务可使用任意字符串操作。</summary>
/// <remarks>不同操作可分配不同范围；同一操作的列表、总数、导出与批量操作须经同一范围入口。</remarks>
public static class DataOperations
{
    /// <summary>读取（列表、详情、统计）。</summary>
    public const string Read = "Read";

    /// <summary>修改。</summary>
    public const string Update = "Update";

    /// <summary>删除。</summary>
    public const string Delete = "Delete";

    /// <summary>导出。</summary>
    public const string Export = "Export";
}

/// <summary>数据范围分配：某个主体在某类资源的某个操作上被授予的一种范围。</summary>
/// <example>
/// 同一资源的读与改可以拿到不同的范围集合：
/// <code>
/// Orders / Read   : All | Own | Organization
/// Orders / Update : Own | Organization
/// </code>
/// </example>
/// <param name="ResourceName">资源类型名称。</param>
/// <param name="Operation">该分配适用的操作。</param>
/// <param name="ScopeName">范围名称。</param>
/// <param name="ScopeValue">范围参数，语义由 Provider 解释（如组织 ID）；组织树展开等业务关系由业务表负责。</param>
public sealed record DataScopeAssignment(
    string ResourceName,
    string Operation,
    string ScopeName,
    string? ScopeValue = null);

/// <summary>数据范围解析上下文。</summary>
/// <param name="Subject">当前主体。</param>
/// <param name="ResourceName">资源类型名称。</param>
/// <param name="Operation">本次查询的操作，见 <see cref="DataOperations"/>。</param>
/// <param name="Assignments">当前主体在该资源该操作上被分配的全部范围。</param>
public sealed record DataScopeContext(
    PermissionSubject Subject,
    string ResourceName,
    string Operation,
    IReadOnlyList<DataScopeAssignment> Assignments);

/// <summary>数据范围提供器：把一种范围翻译成可由数据库执行的查询谓词。</summary>
/// <typeparam name="TEntity">被过滤的实体类型。</typeparam>
/// <remarks>
/// 实现必须返回可被数据库 Provider 翻译的表达式，不得依赖客户端求值或先加载候选数据。
/// 多个被分配的范围之间取并集。
/// </remarks>
public interface IDataScopeProvider<TEntity>
{
    /// <summary>本 Provider 负责的资源类型名称。</summary>
    string ResourceName { get; }

    /// <summary>本 Provider 负责的范围名称。</summary>
    string ScopeName { get; }

    /// <summary>构造该范围对应的查询谓词。</summary>
    /// <returns>非空谓词：“全部可见”显式返回 <c>_ =&gt; true</c>，不贡献可见性返回 <c>_ =&gt; false</c>。</returns>
    ValueTask<Expression<Func<TEntity, bool>>> BuildPredicateAsync(
        DataScopeContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>数据范围分配的来源，由业务项目实现（角色配置表、组织架构或外部策略服务等）。</summary>
public interface IDataScopeAssignmentProvider
{
    /// <summary>获取指定主体在某类资源的某个操作上的全部范围分配；空集合表示没有可见范围（应用器返回空结果集）。</summary>
    ValueTask<IReadOnlyList<DataScopeAssignment>> GetAssignmentsAsync(
        PermissionSubject subject,
        string resourceName,
        string operation,
        CancellationToken cancellationToken = default);
}

/// <summary>数据范围应用入口：把当前主体的可见范围合并进业务查询。</summary>
/// <remarks>列表、总数、导出和批量操作须全部经由本接口取得候选集合，四者才一致。</remarks>
public interface IDataScopeApplier
{
    /// <summary>对查询施加当前主体在该资源上的可见范围。</summary>
    /// <returns>
    /// 施加范围后的查询。主体不可识别时返回空结果集（默认拒绝）；
    /// 主体是超级管理员或被分配的范围中存在"无限制"时，原样返回。
    /// </returns>
    ValueTask<IQueryable<TEntity>> ApplyAsync<TEntity>(
        IQueryable<TEntity> query,
        string resourceName,
        string operation,
        CancellationToken cancellationToken = default);
}
