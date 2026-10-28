using System.Runtime.ExceptionServices;
using Leistd.Data.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Exceptions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 远端解析：按连接名回源控制面，结果按 TTL 缓存在本进程，同租户同名字的并发回源由 HybridCache 合并。
// 只用进程内一级缓存（DisableDistributedCache）：含口令的连接串不进分布式缓存。
// 远端存储不从构造注入而在工厂里自开作用域：工厂为所有等待者服务，不能持有发起者请求作用域里的实例。
// 取消沿用 HybridCache：全部等待者都取消时回源才取消；明确拒绝作废旧值。
internal sealed class RemoteConnectionStringResolver(
    ICurrentTenant currentTenant,
    IConfiguration configuration,
    HybridCache cache,
    IServiceScopeFactory scopeFactory,
    IOptions<TenantRouteCacheOptions> routeCacheOptions,
    TimeProvider? clock = null,
    ILogger<RemoteConnectionStringResolver>? logger = null) : IConnectionStringResolver
{
    public async Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        if (!currentTenant.IsAvailable)
        {
            return TenantConnectionTargets.HostConnection(connectionStringName, configuration);
        }

        var tenantId = currentTenant.Id!.Value;
        // 归一化后做缓存键：Crm 与 crm 是同一条路由
        var name = TenantConnectionNames.Normalize(connectionStringName);
        // 期限同时定义租户改路由前的排空等待时间；启动期已校验
        var lifetime = routeCacheOptions.Value.CacheLifetime;

        try
        {
            return await new TenantRouteCache(cache, clock ?? TimeProvider.System, logger ?? NullLogger<RemoteConnectionStringResolver>.Instance)
                .GetAsync($"leistd:tenant-connection:{tenantId:N}:{name}", lifetime, routeCacheOptions.Value.MaxStaleness,
                    ct => ResolveRemoteAsync(tenantId, connectionStringName, name, ct),
                    static ex => ex is TenantConnectionUnavailableException, cancellationToken);
        }
        catch (TenantConnectionUnavailableException exception)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
    }

    private async ValueTask<string> ResolveRemoteAsync(
        Guid tenantId,
        string connectionStringName,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationStore>();
        var lookup = await store.FindAsync(tenantId, normalizedName, cancellationToken)
            // 租户不存在或已删除。不可通过重试恢复，因此是 404 而不是 503；与本地解析同一判据
            ?? throw new TenantNotFoundException(tenantId.ToString());

        // 在使用与写缓存之前校验响应租户：错误的响应不能造成跨租户数据访问
        if (lookup.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"Tenant connection lookup for '{tenantId}' returned a result for " +
                $"'{lookup.TenantId}'. Refusing to route a tenant to another tenant's database.");
        }

        return TenantConnectionTargets.Select(tenantId, connectionStringName, lookup, configuration);
    }
}
