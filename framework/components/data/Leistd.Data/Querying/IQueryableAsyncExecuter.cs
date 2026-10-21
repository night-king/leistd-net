namespace Leistd.Data.Querying;

/// <summary><see cref="IQueryable{T}"/> 的异步执行入口。</summary>
/// <remarks>由持久化 provider 执行查询，不要求调用方引用其实现。</remarks>
public interface IQueryableAsyncExecuter
{
    /// <summary>异步物化为列表。</summary>
    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>异步统计条数。</summary>
    Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>异步统计条数，结果可能超出 <see cref="int"/> 范围时用本方法。</summary>
    Task<long> LongCountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>异步取第一条；无匹配返回默认值。</summary>
    Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>异步取唯一一条；无匹配返回默认值，多于一条抛 <see cref="InvalidOperationException"/>。</summary>
    Task<T?> SingleOrDefaultAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);

    /// <summary>异步判断是否存在任意一条。</summary>
    Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default);
}
