using Leistd.Ddd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Ddd.Infrastructure.HostedServices;

// 启动时拒绝缺少命名租户过滤器的 DbContext，防止查询隔离和 TenantId 落值同时静默失效。
// 无租户上下文或无法构造的上下文不引入新的启动失败。
internal sealed class MultiTenantFilterGuard(
    IServiceScopeFactory scopeFactory,
    TrackedDbContextTypes trackedDbContextTypes,
    ILogger<MultiTenantFilterGuard> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        if (scope.ServiceProvider.GetService<ICurrentTenant>() is null)
        {
            return Task.CompletedTask;
        }

        foreach (var dbContextType in trackedDbContextTypes.Types)
        {
            var model = TryGetModel(scope.ServiceProvider, dbContextType);
            if (model is null)
            {
                continue;
            }

            // 过滤器定义在根实体上，派生类型沿用该声明。
            var unguarded = model.GetEntityTypes()
                .Where(entityType => entityType.BaseType is null && !entityType.IsOwned())
                .Where(entityType => typeof(IMultiTenant).IsAssignableFrom(entityType.ClrType))
                .Where(entityType =>
                    entityType.FindDeclaredQueryFilter(BaseDbContext.MultiTenantFilterName) is null)
                .Select(entityType => entityType.ClrType.Name)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (unguarded.Count == 0)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"The DbContext '{dbContextType.FullName}' maps multi-tenant entities " +
                $"({string.Join(", ", unguarded)}) but applies no tenant query filter. " +
                "Queries would return every tenant's rows and inserts would leave TenantId null, " +
                "both silently. Derive this DbContext from Leistd.Ddd.Infrastructure.Persistence." +
                "BaseDbContext (it applies the filter and stamps TenantId), or, if the context is " +
                "deliberately host-only, stop mapping these entities into it.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IModel? TryGetModel(IServiceProvider serviceProvider, Type dbContextType)
    {
        try
        {
            return ((DbContext)serviceProvider.GetRequiredService(dbContextType)).Model;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Skipped the multi-tenant filter check for {DbContextType}: the context could not be built",
                dbContextType.FullName);
            return null;
        }
    }
}
