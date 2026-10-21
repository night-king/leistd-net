using Leistd.ObjectMapping.Extensions;
using Leistd.ObjectMapping.Abstractions;
using Leistd.Data.Paging;

namespace Leistd.Ddd.Application.Extensions;

/// <summary>应用层分页对象映射扩展。</summary>
public static class ObjectMapperExtensions
{
    /// <summary>映射分页结果中的条目并保留总数。</summary>
    public static PagedResult<TDestination> MapPagedResult<TSource, TDestination>(
        this IObjectMapper mapper,
        PagedResult<TSource> pagedSource)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(pagedSource);

        var mappedItems = mapper.MapList<TSource, TDestination>(pagedSource.Items);
        return new PagedResult<TDestination>(pagedSource.TotalCount, mappedItems);
    }
}
