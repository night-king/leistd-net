using Leistd.Data.Querying;
using Microsoft.EntityFrameworkCore;

namespace Leistd.Data.EntityFrameworkCore.Querying;

/// <summary>使用 EF Core 执行异步查询。</summary>
public class EfCoreQueryableAsyncExecuter : IQueryableAsyncExecuter
{
    /// <inheritdoc />
    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        return EntityFrameworkQueryableExtensions.ToListAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        return EntityFrameworkQueryableExtensions.CountAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> LongCountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        return EntityFrameworkQueryableExtensions.LongCountAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        return EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> SingleOrDefaultAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        return EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        return EntityFrameworkQueryableExtensions.AnyAsync(query, cancellationToken);
    }
}
