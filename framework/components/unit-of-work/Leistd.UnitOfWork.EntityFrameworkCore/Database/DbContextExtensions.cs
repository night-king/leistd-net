using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Leistd.UnitOfWork.EntityFrameworkCore.Database;

internal static class DbContextExtensions
{
    /// <summary>检查上下文是否使用关系型数据库事务管理器。</summary>
    public static bool HasRelationalTransactionManager(this DbContext dbContext)
    {
        return dbContext.Database.GetService<IDbContextTransactionManager>() is IRelationalTransactionManager;
    }
}
