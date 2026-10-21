using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Stores;

// 连接名解析的唯一口径：精确名优先、默认名兜底、登记过却两者都不命中即解析失败。
// 迁移存储与运行时库目录共用此口径，只回“谁解析到了、谁没解析到”，失败处置由调用方决定。
internal static class TenantConnectionNameResolution
{
    // candidates：名字等于目标名或默认名的全部登记行。
    // registeredTenantIds：登记过任意连接的租户；判"有登记但缺这个名字"必须靠它。
    // 回 Resolved（租户 → 该用哪一条登记）与 Unresolved（登记过、却两个名字都没有的租户，升序）。
    // 两者都不含"一条都没登记"的租户——那种租户使用宿主配置，不是失败。
    internal static (Dictionary<Guid, TenantConnectionRecord> Resolved, List<Guid> Unresolved) Resolve(
        IEnumerable<TenantConnectionRecord> candidates,
        IEnumerable<Guid> registeredTenantIds,
        string normalizedName)
    {
        var defaultName = TenantConnectionNames.Default;

        var resolved = candidates
            .GroupBy(record => record.TenantId)
            .ToDictionary(
                group => group.Key,
                // 精确名优先；没有精确名时只能回落到默认名那一条
                group => group.FirstOrDefault(record => record.Name == normalizedName)
                         ?? group.First(record => record.Name == defaultName));

        var unresolved = registeredTenantIds
            .Where(tenantId => !resolved.ContainsKey(tenantId))
            .Order()
            .ToList();

        return (resolved, unresolved);
    }

    // 解析失败的说明，两处共用
    internal static string DescribeUnresolved(Guid tenantId, string normalizedName)
        => $"Tenant '{tenantId}' has tenant-specific connections registered but none for "
           + $"'{normalizedName}', and no default-named connection to fall back to.";
}
