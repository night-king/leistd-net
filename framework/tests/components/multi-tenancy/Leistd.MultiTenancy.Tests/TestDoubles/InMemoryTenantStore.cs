using Leistd.MultiTenancy.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.MultiTenancy.Tests.TestDoubles;

/// <summary>测试用配置型租户注册表，支持中间件校验与启动期注册探测。</summary>
/// <remarks>不作生产实现：租户启用状态不能依赖重启才更新的配置清单；资源服务关闭注册表校验后不查 Store。</remarks>
internal sealed class InMemoryTenantStore(InMemoryTenantStoreOptions options) : ITenantStore
{
    public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(options.Tenants.FirstOrDefault(t => t.Id == id));

    public Task<TenantConfiguration?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken = default)
        => Task.FromResult(options.Tenants.FirstOrDefault(
            t => string.Equals(t.NormalizedName, normalizedName, StringComparison.Ordinal)));
}

/// <summary>配置 <see cref="InMemoryTenantStore"/> 的租户清单。</summary>
internal sealed class InMemoryTenantStoreOptions
{
    public IList<TenantConfiguration> Tenants { get; } = [];
}

internal static class InMemoryTenantStoreExtensions
{
    /// <summary>注册测试用的配置型租户注册表。</summary>
    public static IServiceCollection AddInMemoryTenantStore(
        this IServiceCollection services,
        Action<InMemoryTenantStoreOptions> configure)
    {
        var options = new InMemoryTenantStoreOptions();
        configure(options);

        services.AddSingleton<ITenantStore>(new InMemoryTenantStore(options));
        return services;
    }
}
