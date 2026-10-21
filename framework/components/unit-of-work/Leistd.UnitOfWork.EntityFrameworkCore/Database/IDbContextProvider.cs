using Microsoft.EntityFrameworkCore;

namespace Leistd.UnitOfWork.EntityFrameworkCore.Database;

/// <summary>取得受当前工作单元管理的 <typeparamref name="TDbContext"/>。</summary>
public interface IDbContextProvider<TDbContext>
    where TDbContext : DbContext
{
    /// <summary>取上下文实例。</summary>
    /// <remarks>
    /// 在工作单元内按上下文类型复用同一实例，并绑定连接归属与物理目标；
    /// 不在工作单元内时每次由当前作用域创建，不建立绑定。
    /// </remarks>
    Task<TDbContext> GetDbContextAsync(CancellationToken cancellationToken = default);
}
