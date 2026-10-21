using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.EntityFrameworkCore.Extensions;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Stores;

/// <summary>直接读取控制库的租户连接存储。</summary>
/// <remarks>
/// 只读未删除租户的连接（经 <see cref="TenantQueryableExtensions"/> 起查）。
/// 返回的连接串已用宿主的 Data Protection 密钥环解密；解密失败抛出，不回退。
/// </remarks>
public class EfCoreTenantConnectionConfigurationStore<TDbContext> : ITenantConnectionConfigurationStore
    where TDbContext : DbContext
{
    private readonly IDbContextProvider<TDbContext> _dbContextProvider;
    private readonly TenantConnectionStringProtector _protector;

    /// <summary>创建存储。</summary>
    /// <param name="dbContextProvider">工作单元内的 DbContext 提供器。</param>
    /// <param name="dataProtectionProvider">宿主的 Data Protection 提供器，用于解密连接串。</param>
    public EfCoreTenantConnectionConfigurationStore(
        IDbContextProvider<TDbContext> dbContextProvider,
        IDataProtectionProvider dataProtectionProvider)
    {
        _dbContextProvider = dbContextProvider;
        _protector = new TenantConnectionStringProtector(dataProtectionProvider);
    }

    /// <inheritdoc />
    public async Task<TenantConnectionLookupResult?> FindAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalized = TenantConnectionNames.Normalize(name);
        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        // 租户不存在（返回 null，调用方失败关闭）与存在但无登记（回落服务自己的配置）必须分开
        var tenantExists = await dbContext.UndeletedTenants()
            .AnyAsync(x => x.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            return null;
        }

        var defaultName = TenantConnectionNames.Default;

        // 一次取回精确名与默认名两行，回落在内存里判，避免两次往返
        var candidates = await dbContext.ConnectionsOfUndeletedTenants()
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Name == normalized || x.Name == defaultName))
            .ToListAsync(cancellationToken);

        var hit = candidates.FirstOrDefault(x => x.Name == normalized)
                  ?? candidates.FirstOrDefault(x => x.Name == defaultName);

        // 命中就说明有；没命中才需要再问一次"到底有没有登记过任何连接"
        var hasAnyConnection = candidates.Count > 0
            || await dbContext.ConnectionsOfUndeletedTenants()
                .AnyAsync(x => x.TenantId == tenantId, cancellationToken);

        return new TenantConnectionLookupResult
        {
            TenantId = tenantId,
            HasAnyConnection = hasAnyConnection,
            Connection = hit is null ? null : ToConfiguration(hit)
        };
    }

    /// <inheritdoc />
    public async Task<TenantMigrationConnectionListResult> GetListAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalized = TenantConnectionNames.Normalize(name);
        var defaultName = TenantConnectionNames.Default;
        var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);

        var candidates = await dbContext.ConnectionsOfUndeletedTenants()
            .AsNoTracking()
            .Where(x => x.Name == normalized || x.Name == defaultName)
            .ToListAsync(cancellationToken);

        // 登记过连接的租户全集：解析不出这个名字的单列为失败，不静默跳过
        var tenantIds = await dbContext.ConnectionsOfUndeletedTenants()
            .AsNoTracking()
            .Select(x => x.TenantId)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var (resolved, unresolved) = TenantConnectionNameResolution.Resolve(candidates, tenantIds, normalized);
        var failures = unresolved
            .Select(tenantId => new TenantDatabaseFailure(
                tenantId, TenantConnectionNameResolution.DescribeUnresolved(tenantId, normalized)))
            .ToList();

        var connections = new List<TenantMigrationConnection>(tenantIds.Count);
        foreach (var tenantId in tenantIds)
        {
            if (!resolved.TryGetValue(tenantId, out var record))
            {
                continue;
            }

            // 解不开密文（密钥环没共享、密文被改）同样只记在这个租户名下：保护器已把它换成只带租户与连接名的安全消息
            string connectionString;
            try
            {
                connectionString = _protector.Unprotect(tenantId, record.Name, record.ProtectedConnectionString);
            }
            catch (InvalidOperationException exception)
            {
                failures.Add(new TenantDatabaseFailure(tenantId, exception.Message));
                continue;
            }

            connections.Add(new TenantMigrationConnection(tenantId, record.Name, connectionString));
        }

        return new TenantMigrationConnectionListResult(connections, failures);
    }

    private TenantConnectionConfiguration ToConfiguration(TenantConnectionRecord record) => new()
    {
        TenantId = record.TenantId,
        Name = record.Name,
        ConnectionString = _protector.Unprotect(record.TenantId, record.Name, record.ProtectedConnectionString),
        Version = record.Version
    };
}
