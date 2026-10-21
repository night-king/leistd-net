using Mapster;
using MapsterMapper;
using Leistd.ObjectMapping.Abstractions;

namespace Leistd.ObjectMapping.Mapster.Services;

/// <summary>使用 Mapster 执行对象映射。</summary>
public class MapsterObjectMapper(IMapper mapper) : IObjectMapper
{
    /// <inheritdoc />
    public TDestination Map<TSource, TDestination>(TSource source)
    {
        return mapper.Map<TSource, TDestination>(source);
    }

    /// <inheritdoc />
    public TDestination Map<TSource, TDestination>(TSource source, IDictionary<string, object> contextItems)
    {
        using (new MapContextScope())
        {
            if (contextItems != null && MapContext.Current != null)
            {
                foreach (var item in contextItems)
                {
                    MapContext.Current.Parameters[item.Key] = item.Value;
                }
            }
            return mapper.Map<TSource, TDestination>(source);
        }
    }

    /// <inheritdoc />
    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        return mapper.Map(source, destination);
    }
}
