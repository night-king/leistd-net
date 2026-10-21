using Leistd.UnitOfWork.Database;
using Microsoft.EntityFrameworkCore;

namespace Leistd.UnitOfWork.EntityFrameworkCore.Database;

/// <summary>把一个受工作单元管理的 <see cref="DbContext"/> 挂到工作单元上。</summary>
/// <remarks>上下文由工作单元的 DI 作用域创建与回收，本类不实现 <see cref="IDisposable"/>（见 <see cref="IDatabaseApi"/>）。</remarks>
public class EfCoreDatabaseApi<TDbContext>(TDbContext dbContext) : IDatabaseApi, ISupportsSavingChanges, ISupportsRollback
    where TDbContext : DbContext
{
    /// <summary>本 API 持有的上下文实例。</summary>
    public TDbContext DbContext { get; } = dbContext;

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await DbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 空操作：EF Core 没有上下文级回滚，事务的回滚由 <see cref="EfCoreTransactionApi"/> 执行。
    /// </remarks>
    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
