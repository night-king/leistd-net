using Leistd.Data.Connections;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.EntityFrameworkCore.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;

// 本地解析：宿主自己持有控制库，直接按连接名查询租户连接并解密。
// 控制库上下文必须直接注入而不经 IDbContextProvider：Provider 创建任何 DbContext 前都会调用本解析器，否则形成递归。
internal sealed class LocalConnectionStringResolver<TControlDbContext>(
    ICurrentTenant currentTenant,
    TControlDbContext controlDbContext,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration,
    IOptions<LocalTenantConnectionOptions> options) : IConnectionStringResolver
    where TControlDbContext : DbContext
{
    private readonly TenantConnectionStringProtector _protector = new(dataProtectionProvider);

    public async Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        // 控制库始终使用宿主连接，避免租户路由的循环依赖
        var controlName = options.Value.ControlPlaneConnectionStringName!;
        if (string.Equals(connectionStringName, controlName, StringComparison.Ordinal))
        {
            return ControlPlaneConnection(controlName);
        }

        if (!currentTenant.IsAvailable)
        {
            return TenantConnectionTargets.HostConnection(connectionStringName, configuration);
        }

        var tenantId = currentTenant.Id!.Value;
        var name = TenantConnectionNames.Normalize(connectionStringName);

        // 控制库上下文通常没有软删除过滤器，必须从"未删除租户"入口起查
        var tenantExists = await controlDbContext.UndeletedTenants()
            .AsNoTracking()
            .AnyAsync(x => x.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            // 租户不存在或已删除，不可通过重试恢复，因此是 404 而不是 503
            throw new TenantNotFoundException(tenantId.ToString());
        }

        var defaultName = TenantConnectionNames.Default;
        var candidates = await controlDbContext.ConnectionsOfUndeletedTenants()
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Name == name || x.Name == defaultName))
            .ToListAsync(cancellationToken);

        var hit = candidates.FirstOrDefault(x => x.Name == name)
                  ?? candidates.FirstOrDefault(x => x.Name == defaultName);

        var hasAnyConnection = candidates.Count > 0
            || await controlDbContext.ConnectionsOfUndeletedTenants()
                .AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId, cancellationToken);

        var lookup = new TenantConnectionLookupResult
        {
            TenantId = tenantId,
            HasAnyConnection = hasAnyConnection,
            Connection = hit is null
                ? null
                : new TenantConnectionConfiguration
                {
                    TenantId = tenantId,
                    Name = hit.Name,
                    ConnectionString = _protector.Unprotect(tenantId, hit.Name, hit.ProtectedConnectionString),
                    Version = hit.Version
                }
        };

        return TenantConnectionTargets.Select(tenantId, connectionStringName, lookup, configuration);
    }

    private string ControlPlaneConnection(string controlName) =>
        Configured(configuration.GetConnectionString(controlName))
        ?? Configured(configuration.GetConnectionString(ConnectionStringNames.Default))
        ?? throw new InvalidOperationException(
            $"Neither ConnectionStrings:{controlName} nor ConnectionStrings:{ConnectionStringNames.Default} is configured.");

    private static string? Configured(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
