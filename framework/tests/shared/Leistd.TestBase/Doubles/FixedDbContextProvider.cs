using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;

namespace Leistd.TestBase.Doubles;

/// <summary>返回固定 <see cref="DbContext"/> 实例的提供器，供不经工作单元的单元测试使用。</summary>
/// <remarks>
/// 组件的 EF 存储与管理器一律经 <see cref="IDbContextProvider{TDbContext}"/> 取上下文
/// （只有它会设置 <c>DbContextCreationContext.Current</c>，从而拿到工作单元已解析的连接）。
/// 单元测试直接 <c>new</c> 一个 InMemory 上下文时，用本类把它包成提供器即可，
/// 不必为此搭一整套工作单元。
/// </remarks>
public sealed class FixedDbContextProvider<TDbContext>(TDbContext dbContext) : IDbContextProvider<TDbContext>
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public Task<TDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(dbContext);
}

/// <summary><see cref="FixedDbContextProvider{TDbContext}"/> 的构造快捷方式，省去重复写泛型参数。</summary>
/// <remarks>用法：<c>using static Leistd.TestBase.Doubles.DbContextProviderFor;</c> 后写 <c>Fixed(db)</c>。</remarks>
public static class DbContextProviderFor
{
    /// <summary>把已有上下文实例包成提供器。</summary>
    public static IDbContextProvider<TDbContext> Fixed<TDbContext>(TDbContext dbContext)
        where TDbContext : DbContext
        => new FixedDbContextProvider<TDbContext>(dbContext);
}
