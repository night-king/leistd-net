using Leistd.ObjectMapping.Abstractions;

namespace Leistd.ObjectMapping.Extensions;

/// <summary><see cref="IObjectMapper"/> 的集合映射扩展。</summary>
public static class ObjectMapperExtensions
{
    /// <summary>将对象序列映射为目标类型列表。</summary>
    public static List<TDestination> MapList<TSource, TDestination>(
        this IObjectMapper mapper,
        IEnumerable<TSource> sources)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(sources);

        return sources.Select(mapper.Map<TSource, TDestination>).ToList();
    }
}
