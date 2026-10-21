using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Authorization.DataScope.Services;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.DataScope.Abstractions;

namespace Leistd.Authorization.DataScope;

/// <summary>数据范围注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册数据范围应用器。</summary>
    /// <remarks>
    /// 依赖调用方已注册 <see cref="IPermissionSubjectProvider"/> 与
    /// <see cref="IDataScopeAssignmentProvider"/>；后者由业务项目提供，
    /// 决定"当前主体在某类资源上被分配了哪些范围"。
    /// 可重复调用：服务只注册一次。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddDataScopeCore();
    /// builder.Services.AddDataScopeProvider&lt;Order, OrderOwnDataScopeProvider&gt;();
    ///
    /// // 集合级入口：列表、总数、导出必须全部经它取候选集
    /// var scoped = await dataScopeApplier.ApplyAsync(query, "Orders", "Read", ct);
    /// </code>
    /// </example>
    public static IServiceCollection AddDataScopeCore(this IServiceCollection services)
    {
        services.TryAddScoped<IDataScopeApplier, DefaultDataScopeApplier>();
        return services;
    }

    /// <summary>注册某个实体的一种数据范围 Provider；同一实体可以注册多个不同范围。</summary>
    /// <remarks>按 Provider 类型登记：同一 Provider 重复调用只登记一次，不同 Provider 并存。</remarks>
    public static IServiceCollection AddDataScopeProvider<TEntity, TProvider>(
        this IServiceCollection services)
        where TProvider : class, IDataScopeProvider<TEntity>
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDataScopeProvider<TEntity>, TProvider>());
        return services;
    }
}
