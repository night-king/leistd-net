namespace Leistd.ObjectMapping.Abstractions;

/// <summary>在对象模型之间复制已配置的成员。</summary>
/// <example>
/// <code>
/// var dto = objectMapper.Map&lt;Order, OrderDto&gt;(order);
///
/// objectMapper.Map(input, existingOrder);
/// </code>
/// </example>
public interface IObjectMapper
{
    /// <summary>将源对象映射为新目标实例。</summary>
    TDestination Map<TSource, TDestination>(TSource source);

    /// <summary>使用本次调用的上下文数据创建目标实例。</summary>
    TDestination Map<TSource, TDestination>(TSource source, IDictionary<string, object> contextItems);

    /// <summary>将源对象映射到现有目标实例。</summary>
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);
}
