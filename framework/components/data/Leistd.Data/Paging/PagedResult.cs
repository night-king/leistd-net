using System.Collections.ObjectModel;

namespace Leistd.Data.Paging;

/// <summary>一页查询结果。</summary>
/// <typeparam name="T">条目类型。</typeparam>
/// <param name="TotalCount">符合条件的总条数，与当前页条数无关。</param>
/// <param name="Items">当前页条目。</param>
public record PagedResult<T>(long TotalCount, IReadOnlyList<T> Items)
{
    /// <summary>从任意序列构造分页结果；<paramref name="items"/> 为 <see langword="null"/> 时得到空集合。</summary>
    /// <param name="totalCount">符合条件的总条数。</param>
    /// <param name="items">当前页条目。</param>
    public PagedResult(long totalCount, IEnumerable<T> items)
        : this(totalCount, items?.ToList().AsReadOnly() ?? ReadOnlyCollection<T>.Empty)
    {
    }

    /// <summary>没有任何条目的结果。</summary>
    public static PagedResult<T> Empty { get; } = new(0, ReadOnlyCollection<T>.Empty);
}
