using Leistd.Data.EntityFrameworkCore.Querying;
using Leistd.Data.Querying;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.Data.EntityFrameworkCore;

/// <summary>原生 EF Core 查询执行器的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册单例查询执行器，不覆盖宿主实现。</summary>
    public static IServiceCollection AddDataEfCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IQueryableAsyncExecuter, EfCoreQueryableAsyncExecuter>();
        return services;
    }
}
