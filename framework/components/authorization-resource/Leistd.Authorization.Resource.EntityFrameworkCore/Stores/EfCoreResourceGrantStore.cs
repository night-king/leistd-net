using Microsoft.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Leistd.Authorization.Resource.Grants;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Exceptions;
using Leistd.Authorization.Resource.EntityFrameworkCore.Entities;

namespace Leistd.Authorization.Resource.EntityFrameworkCore.Stores;

/// <summary>EF Core 资源 ACL 存储。</summary>
/// <remarks>
/// DbContext 经 <see cref="IDbContextProvider{TDbContext}"/> 获取：<c>Query*ResourceKeysAsync</c> 返回的查询须与业务查询出自同一个
/// DbContext 实例，EF 才能翻译进同一条 SQL。
/// </remarks>
public class EfCoreResourceGrantStore<TDbContext>(IDbContextProvider<TDbContext> dbContextProvider)
    : IResourceGrantStore
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public async Task<ResourceGrantSet> GetGrantsAsync(
        string resourceName,
        string resourceKey,
        CancellationToken cancellationToken = default)
    {
        // 版本前后读确保 ACL 快照未被并发写入撕裂。
        const int MaxAttempts = 5;

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            var before = await ReadVersionAsync();

            var grants = await dbContext.Set<ResourcePermissionGrantRecord>()
                .AsNoTracking()
                .Where(x => x.ResourceName == resourceName && x.ResourceKey == resourceKey)
                .Select(x => new ResourceGrant(x.Operation, x.ProviderName, x.ProviderKey, x.Effect))
                .ToListAsync(cancellationToken);

            var after = await ReadVersionAsync();

            if (before == after)
                return new ResourceGrantSet(resourceName, resourceKey, grants, after);

            if (attempt >= MaxAttempts)
            {
                // 读取不稳定时失败，避免调用方基于撕裂快照覆盖并发修改。
                throw new UnstableGrantSnapshotException($"{resourceName}/{resourceKey}", attempt);
            }
        }

        Task<long> ReadVersionAsync()
            => dbContext.Set<ResourceAuthorizationVersionRecord>()
                .AsNoTracking()
                .Where(x => x.ResourceName == resourceName && x.ResourceKey == resourceKey)
                .Select(x => x.Version)
                .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ResourceGrantEffect>> GetEffectiveGrantsAsync(
        string resourceName,
        string resourceKey,
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default)
    {
        var roleKeys = Normalize(roleIds);
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var grants = await dbContext.Set<ResourcePermissionGrantRecord>()
            .AsNoTracking()
            .Where(x => x.ResourceName == resourceName
                        && x.ResourceKey == resourceKey
                        && ((x.ProviderName == PermissionGrantProviderNames.User && x.ProviderKey == userId)
                            || (x.ProviderName == PermissionGrantProviderNames.Role && roleKeys.Contains(x.ProviderKey))))
            .OrderBy(x => x.Id)
            .Select(x => new { x.Operation, x.Effect })
            .ToListAsync(cancellationToken);

        var effects = new Dictionary<string, ResourceGrantEffect>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            // 强度为非法值、拒绝、允许；更强结果不可被后续记录覆盖。
            // 未知存储值失败关闭，且结果不受数据库返回顺序影响。
            if (effects.TryGetValue(grant.Operation, out var existing) &&
                Strength(existing) >= Strength(grant.Effect))
            {
                continue;
            }

            effects[grant.Operation] = grant.Effect;
        }

        return effects;

        static int Strength(ResourceGrantEffect effect) => effect switch
        {
            ResourceGrantEffect.Granted => 0,
            ResourceGrantEffect.Prohibited => 1,
            _ => 2
        };
    }

    /// <inheritdoc />
    public async Task<IQueryable<string>> QueryGrantedResourceKeysAsync(
        string resourceName,
        string operation,
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default)
    {
        var roleKeys = Normalize(roleIds);
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var records = dbContext.Set<ResourcePermissionGrantRecord>().AsNoTracking();

        var subjectGrants = records.Where(x =>
            x.ResourceName == resourceName
            && x.Operation == operation
            && ((x.ProviderName == PermissionGrantProviderNames.User && x.ProviderKey == userId)
                || (x.ProviderName == PermissionGrantProviderNames.Role && roleKeys.Contains(x.ProviderKey))));

        // 拒绝优先；未知 effect 也按拒绝处理，使列表查询与单资源判定一致。
        var deniedKeys = subjectGrants
            .Where(x => x.Effect != ResourceGrantEffect.Granted)
            .Select(x => x.ResourceKey);

        return subjectGrants
            .Where(x => x.Effect == ResourceGrantEffect.Granted && !deniedKeys.Contains(x.ResourceKey))
            .Select(x => x.ResourceKey)
            .Distinct();
    }

    /// <inheritdoc />
    public async Task<IQueryable<string>> QueryDeniedResourceKeysAsync(
        string resourceName,
        string operation,
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default)
    {
        var roleKeys = Normalize(roleIds);
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        // 与判定端一致，非 Granted 值均视为拒绝。
        return dbContext.Set<ResourcePermissionGrantRecord>()
            .AsNoTracking()
            .Where(x => x.ResourceName == resourceName
                        && x.Operation == operation
                        && x.Effect != ResourceGrantEffect.Granted
                        && ((x.ProviderName == PermissionGrantProviderNames.User && x.ProviderKey == userId)
                            || (x.ProviderName == PermissionGrantProviderNames.Role && roleKeys.Contains(x.ProviderKey))))
            .Select(x => x.ResourceKey)
            .Distinct();
    }

    private static List<string> Normalize(IEnumerable<string> values)
        => values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
}
