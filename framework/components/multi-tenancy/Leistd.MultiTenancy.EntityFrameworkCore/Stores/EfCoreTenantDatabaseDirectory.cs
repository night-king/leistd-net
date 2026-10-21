using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.Extensions;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Stores;

// 宿主自己持有控制库时的库目录：直接读注册表与连接登记。
// 指纹按解密后的连接串算，与迁移目标同一算法；连接串只用于算指纹，不出这个类。
internal sealed class EfCoreTenantDatabaseDirectory<TDbContext>(
    IDbContextProvider<TDbContext> dbContextProvider,
    IDataProtectionProvider dataProtectionProvider) : ITenantDatabaseDirectory
    where TDbContext : DbContext
{
    private readonly TenantConnectionStringProtector _protector = new(dataProtectionProvider);

    public async Task<TenantDatabaseListResult> GetDatabasesAsync(
        string name,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        var normalized = TenantConnectionNames.Normalize(name);
        var defaultName = TenantConnectionNames.Default;
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var tenants = await dbContext.UndeletedTenants()
            .AsNoTracking()
            .Where(tenant => !activeOnly || tenant.IsActive)
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
        if (tenants.Count == 0)
        {
            return TenantDatabaseListResult.Empty;
        }

        var candidates = await dbContext.ConnectionsOfUndeletedTenants()
            .AsNoTracking()
            .Where(record => (record.Name == normalized || record.Name == defaultName) && tenants.Contains(record.TenantId))
            .ToListAsync(cancellationToken);

        // 登记过任意连接的租户：用于区分“住在宿主库”与“有独立库但这个连接名解析不出”（后者是失败，与运行时一致）
        var registered = await dbContext.ConnectionsOfUndeletedTenants()
            .AsNoTracking()
            .Where(record => tenants.Contains(record.TenantId))
            .Select(record => record.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var (resolved, unresolved) = TenantConnectionNameResolution.Resolve(candidates, registered, normalized);

        var byFingerprint = new SortedDictionary<string, List<Guid>>(StringComparer.Ordinal);
        var failures = new List<TenantDatabaseFailure>();

        // 解析不出连接名只隔离该租户，其余库照常执行
        failures.AddRange(unresolved.Select(tenantId =>
            new TenantDatabaseFailure(tenantId, TenantConnectionNameResolution.DescribeUnresolved(tenantId, normalized))));

        foreach (var tenantId in tenants.Order())
        {
            // 无任何登记即住在宿主库，既不是失败也不列出（清单可能很大，进宿主库用宿主配置）
            if (!resolved.TryGetValue(tenantId, out var record))
            {
                continue;
            }

            // 解密失败（密钥环换了、密文损坏）只跳过这一个租户
            string connectionString;
            try
            {
                connectionString = _protector.Unprotect(tenantId, record.Name, record.ProtectedConnectionString);
            }
            catch (Exception exception)
            {
                failures.Add(new TenantDatabaseFailure(tenantId, exception.Message));
                continue;
            }

            var fingerprint = TenantDatabaseFingerprint.Of(connectionString);
            if (!byFingerprint.TryGetValue(fingerprint, out var members))
            {
                members = [];
                byFingerprint[fingerprint] = members;
            }

            members.Add(tenantId);
        }

        return new TenantDatabaseListResult(
            [.. byFingerprint.Select(pair => new TenantDatabaseEntry(pair.Key, [.. pair.Value.Order()]))],
            failures);
    }
}
