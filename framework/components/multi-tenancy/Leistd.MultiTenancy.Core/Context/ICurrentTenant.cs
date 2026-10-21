namespace Leistd.MultiTenancy.Context;

/// <summary>提供当前异步流的租户上下文。</summary>
public interface ICurrentTenant
{
    /// <summary>当前是否处于租户（而非宿主）上下文。</summary>
    bool IsAvailable { get; }

    /// <summary>当前租户标识；<see langword="null"/> 表示宿主。</summary>
    Guid? Id { get; }

    /// <summary>当前租户名称，仅用于展示与日志。</summary>
    string? Name { get; }

    /// <summary>切换当前租户，并在释放返回句柄时恢复父上下文。</summary>
    /// <param name="id">目标租户 Id；<see langword="null"/> 切换到宿主视角。</param>
    /// <param name="name">目标租户名称，可省略。</param>
    /// <example>
    /// <code>
    /// // 后台任务里切到指定租户，作用域退出即还原
    /// using (currentTenant.Change(tenantId))
    /// {
    ///     await repository.InsertAsync(entity, ct);   // TenantId 自动落值、查询自动过滤
    /// }
    ///
    /// // 切到宿主上下文
    /// using (currentTenant.Change(null)) { /* 只见宿主行 */ }
    /// </code>
    /// </example>
    IDisposable Change(Guid? id, string? name = null);
}
