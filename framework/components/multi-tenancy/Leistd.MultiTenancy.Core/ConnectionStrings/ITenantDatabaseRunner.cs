namespace Leistd.MultiTenancy.ConnectionStrings;

/// <summary>在宿主库与每个独立库里各执行一次同一段逻辑，逐库隔离失败。</summary>
/// <remarks>
/// <para>回调运行时已切到该库的代表租户上下文。执行器不开工作单元：需要事务时在回调里
/// <c>Begin(requiresNew: true)</c> 后经 <c>IDbContextProvider</c> 取上下文。</para>
/// <para>一个库失败只记日志并计入结果，其余库照常执行；调用方取消时停止并抛出。</para>
/// </remarks>
/// <example>
/// <code>
/// // activeOnly: false —— 保留期作业要连停用租户的库一起处理，合规义务不随停用消失
/// var result = await runner.ForEachDatabaseAsync(ConnectionStringNames.Default, activeOnly: false, async (database, ct) =&gt;
/// {
///     using var uow = unitOfWorkManager.Begin(requiresNew: true);
///     var dbContext = await dbContextProvider.GetDbContextAsync(ct);
///     // 同库其余租户的数据由 IgnoreQueryFilters() 一并覆盖
///     await uow.CompleteAsync(ct);
/// }, cancellationToken);
/// </code>
/// </example>
public interface ITenantDatabaseRunner
{
    /// <summary>对指定连接名下的每个物理库执行 <paramref name="action"/>。</summary>
    /// <param name="activeOnly">只处理启用租户的库；必须显式给出，理由见 <see cref="ITenantDatabaseEnumerator"/>。</param>
    /// <param name="connectionStringName">连接名，通常是业务 DbContext 的 <c>[ConnectionStringName]</c>。</param>
    /// <param name="action">在某个库的租户上下文里执行的逻辑。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理的库数、失败的库，以及本轮解析不出连接、被跳过的租户。</returns>
    Task<TenantDatabaseRunResult> ForEachDatabaseAsync(
        string connectionStringName,
        bool activeOnly,
        Func<TenantDatabase, CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}

/// <summary>一次逐库执行的结果。</summary>
/// <param name="Databases">处理的物理库数（含宿主库）。</param>
/// <param name="UnresolvedTenants">解析不出连接、本轮被跳过的租户。</param>
/// <param name="FailedDatabases">执行失败的库；错误已记日志。</param>
public sealed record TenantDatabaseRunResult(
    int Databases,
    IReadOnlyList<TenantDatabase> FailedDatabases,
    IReadOnlyList<TenantDatabaseFailure> UnresolvedTenants);
