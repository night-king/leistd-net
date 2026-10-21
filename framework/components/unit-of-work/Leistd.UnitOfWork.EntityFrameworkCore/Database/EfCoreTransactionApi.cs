using Leistd.UnitOfWork.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Leistd.UnitOfWork.EntityFrameworkCore.Database;

/// <summary>把一个 EF Core 事务挂到工作单元上，并在提交与回滚时带上加入该事务的其它上下文。</summary>
/// <remarks>事务归工作单元所有，工作单元释放时一并释放（见 <see cref="IDatabaseApi"/>）。</remarks>
public class EfCoreTransactionApi(IDbContextTransaction dbContextTransaction, DbContext starterDbContext)
    : ITransactionApi, ISupportsRollback
{
    /// <summary>底层 EF Core 事务。</summary>
    public IDbContextTransaction DbContextTransaction { get; } = dbContextTransaction;

    /// <summary>开启本事务的上下文；提交与回滚由它执行。</summary>
    public DbContext StarterDbContext { get; } = starterDbContext;

    /// <summary>加入本事务的其它上下文，提交前一并 <c>SaveChanges</c>。</summary>
    public List<DbContext> AttendedDbContexts { get; } = new();

    /// <inheritdoc />
    public async Task CommitAsync()
    {
        foreach (var dbContext in AttendedDbContexts)
        {
            // 共享同一连接的关系型上下文已加入主事务，随主事务一起提交
            if (dbContext.HasRelationalTransactionManager() &&
                dbContext.Database.GetDbConnection() == DbContextTransaction.GetDbTransaction().Connection)
            {
                continue;
            }

            await dbContext.Database.CommitTransactionAsync();
        }

        await DbContextTransaction.CommitAsync();
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        foreach (var dbContext in AttendedDbContexts)
        {
            // 共享同一连接的关系型上下文随主事务回滚
            if (dbContext.HasRelationalTransactionManager() &&
                dbContext.Database.GetDbConnection() == DbContextTransaction.GetDbTransaction().Connection)
            {
                continue;
            }

            await dbContext.Database.RollbackTransactionAsync(cancellationToken);
        }

        await DbContextTransaction.RollbackAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DbContextTransaction.Dispose();
    }
}
