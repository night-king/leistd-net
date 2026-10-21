namespace Leistd.Data.Filters;

/// <summary>控制指定类型的数据过滤器状态。</summary>
/// <typeparam name="TFilter">过滤器标记类型。</typeparam>
public interface IDataFilter<TFilter>
    where TFilter : class
{
    /// <summary>在返回的作用域内禁用过滤器。</summary>
    /// <returns>释放时恢复先前状态的作用域。</returns>
    IDisposable Disable();

    /// <summary>在返回的作用域内启用过滤器。</summary>
    /// <returns>释放时恢复先前状态的作用域。</returns>
    IDisposable Enable();

    /// <summary>过滤器当前是否启用；未显式设置时为 <see langword="true"/>。</summary>
    bool IsEnabled { get; }
}

/// <summary>按过滤器标记类型控制数据过滤状态。</summary>
public interface IDataFilter
{
    /// <summary>在返回的作用域内禁用指定过滤器。</summary>
    IDisposable Disable<TFilter>() where TFilter : class;

    /// <summary>在返回的作用域内启用指定过滤器。</summary>
    IDisposable Enable<TFilter>() where TFilter : class;

    /// <summary>指定过滤器当前是否启用。</summary>
    bool IsEnabled<TFilter>() where TFilter : class;
}
