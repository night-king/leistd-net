using Microsoft.Extensions.DependencyInjection;
using Leistd.Disposables;
using System.Collections.Concurrent;

namespace Leistd.Data.Filters;

/// <summary>按标记类型解析并控制数据过滤器。</summary>
public class DataFilter(IServiceProvider serviceProvider) : IDataFilter
{
    private readonly ConcurrentDictionary<Type, object> _filters = new();

    /// <inheritdoc />
    public IDisposable Disable<TFilter>() where TFilter : class
    {
        return GetFilter<TFilter>().Disable();
    }

    /// <inheritdoc />
    public IDisposable Enable<TFilter>() where TFilter : class
    {
        return GetFilter<TFilter>().Enable();
    }

    /// <inheritdoc />
    public bool IsEnabled<TFilter>() where TFilter : class
    {
        return GetFilter<TFilter>().IsEnabled;
    }

    private IDataFilter<TFilter> GetFilter<TFilter>() where TFilter : class
    {
        return (_filters.GetOrAdd(
            typeof(TFilter),
            _ => serviceProvider.GetRequiredService<IDataFilter<TFilter>>()
        ) as IDataFilter<TFilter>)!;
    }
}

/// <summary>在当前异步上下文中管理指定数据过滤器的嵌套状态。</summary>
/// <remarks>
/// 每次开关都为当前异步流写入新状态，释放作用域时写回进入前的状态。
/// 并行分支各自继承分叉时的状态，分支内的开关不影响父流程或兄弟分支。
/// </remarks>
/// <typeparam name="TFilter">过滤器标记类型。</typeparam>
public class DataFilter<TFilter> : IDataFilter<TFilter>
    where TFilter : class
{
    // 只存不可变值：AsyncLocal 复制的是引用，存可变对象会让分叉后的分支共享同一份状态
    private readonly AsyncLocal<bool?> _isEnabled = new();

    /// <inheritdoc />
    public bool IsEnabled => _isEnabled.Value ?? true;

    /// <inheritdoc />
    public IDisposable Disable()
    {
        return SetIsEnabled(false);
    }

    /// <inheritdoc />
    public IDisposable Enable()
    {
        return SetIsEnabled(true);
    }

    private IDisposable SetIsEnabled(bool isEnabled)
    {
        var previous = _isEnabled.Value;
        _isEnabled.Value = isEnabled;

        return new DisposeAction(() => _isEnabled.Value = previous);
    }
}
