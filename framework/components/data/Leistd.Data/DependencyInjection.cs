using Leistd.Data.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.Data;

/// <summary>数据过滤状态的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册按异步上下文隔离的数据过滤状态，不覆盖宿主实现。</summary>
    /// <remarks>两个端口均为单例；默认启用，开关只在返回的嵌套作用域内生效。</remarks>
    public static IServiceCollection AddDataFilters(this IServiceCollection services)
    {
        services.TryAddSingleton<IDataFilter, DataFilter>();
        services.TryAddSingleton(typeof(IDataFilter<>), typeof(DataFilter<>));
        return services;
    }
}
