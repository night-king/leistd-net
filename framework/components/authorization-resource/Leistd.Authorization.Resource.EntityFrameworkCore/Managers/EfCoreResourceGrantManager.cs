using Microsoft.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Leistd.Authorization.Resource.Grants;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Resource.EntityFrameworkCore.Entities;
using Leistd.Authorization.Resource.Exceptions;

namespace Leistd.Authorization.Resource.EntityFrameworkCore.Managers;

/// <summary>EF Core 资源 ACL 管理器。</summary>
/// <remarks>DbContext 经 <see cref="IDbContextProvider{TDbContext}"/> 获取，写入随工作单元事务并落在当前租户的库。</remarks>
public class EfCoreResourceGrantManager<TDbContext>(IDbContextProvider<TDbContext> dbContextProvider)
    : IResourceGrantManager
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public async Task<long> ReplaceGrantsAsync(
        string resourceName,
        string resourceKey,
        IReadOnlyCollection<ResourceGrant> grants,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceName) || string.IsNullOrWhiteSpace(resourceKey))
            throw new InvalidResourceGrantException(resourceName, resourceKey, "resource name and key are required");

        foreach (var grant in grants)
        {
            // 在写入口拒绝非法枚举值，避免读取端失败关闭后静默拒绝所有主体。
            if (!Enum.IsDefined(grant.Effect))
                throw new InvalidResourceGrantException(
                    resourceName, resourceKey, $"effect '{grant.Effect}' is not a defined value");

            // 读取端只匹配 User 和 Role，因此写入端拒绝其他主体类型。
            if (grant.ProviderName != PermissionGrantProviderNames.User &&
                grant.ProviderName != PermissionGrantProviderNames.Role)
            {
                throw new InvalidResourceGrantException(
                    resourceName, resourceKey, $"unknown provider '{grant.ProviderName}'");
            }

            if (string.IsNullOrWhiteSpace(grant.ProviderKey) || string.IsNullOrWhiteSpace(grant.Operation))
            {
                throw new InvalidResourceGrantException(
                    resourceName, resourceKey, "operation and provider key are required");
            }
        }

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var set = dbContext.Set<ResourcePermissionGrantRecord>();
        var versionSet = dbContext.Set<ResourceAuthorizationVersionRecord>();

        var version = await versionSet.FirstOrDefaultAsync(
            x => x.ResourceName == resourceName && x.ResourceKey == resourceKey,
            cancellationToken);

        var currentVersion = version?.Version ?? 0;
        if (expectedVersion.HasValue && expectedVersion.Value != currentVersion)
        {
            throw new ResourceGrantConcurrencyException(
                resourceName,
                resourceKey,
                expectedVersion.Value,
                currentVersion);
        }

        var existing = await set
            .Where(x => x.ResourceName == resourceName && x.ResourceKey == resourceKey)
            .ToListAsync(cancellationToken);

        var target = new Dictionary<(string Operation, string ProviderName, string ProviderKey), ResourceGrantEffect>();
        foreach (var grant in grants)
        {
            var key = (grant.Operation, grant.ProviderName, grant.ProviderKey);

            // 同一三元组重复出现时拒绝优先。
            if (target.TryGetValue(key, out var current) && current == ResourceGrantEffect.Prohibited)
                continue;

            target[key] = grant.Effect;
        }

        // 精确记录本次改动，防止失败条目污染宿主后续的 SaveChanges。
        var touched = new List<EntityEntry<ResourcePermissionGrantRecord>>();

        foreach (var record in existing)
        {
            var key = (record.Operation, record.ProviderName, record.ProviderKey);
            if (!target.TryGetValue(key, out var effect))
            {
                touched.Add(set.Remove(record));
            }
            else if (record.Effect != effect)
            {
                record.Effect = effect;
                touched.Add(dbContext.Entry(record));
            }
        }

        var existingKeys = existing
            .Select(x => (x.Operation, x.ProviderName, x.ProviderKey))
            .ToHashSet();

        foreach (var ((operation, providerName, providerKey), effect) in target)
        {
            if (existingKeys.Contains((operation, providerName, providerKey)))
                continue;

            touched.Add(set.Add(new ResourcePermissionGrantRecord
            {
                ResourceName = resourceName,
                ResourceKey = resourceKey,
                Operation = operation,
                ProviderName = providerName,
                ProviderKey = providerKey,
                Effect = effect
            }));
        }

        if (touched.Count == 0)
            return currentVersion;

        var isFirstWrite = version == null;
        if (version == null)
        {
            version = new ResourceAuthorizationVersionRecord
            {
                ResourceName = resourceName,
                ResourceKey = resourceKey,
                Version = 1
            };
            await versionSet.AddAsync(version, cancellationToken);
        }
        else
        {
            version.Version += 1;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex) when (InvolvesThisResource(ex))
        {
            DiscardPendingChanges();
            throw await ConflictAsync();
        }
        catch (DbUpdateException) when (isFirstWrite)
        {
            // 首次写入只有在版本行已被并发创建时才映射为并发异常。
            DiscardPendingChanges();

            var conflict = await ConflictAsync();
            if (conflict.ActualVersion == 0)
                throw;

            throw conflict;
        }

        return version.Version;

        bool InvolvesThisResource(DbUpdateConcurrencyException exception)
            => exception.Entries.Any(entry => entry.Entity switch
            {
                ResourceAuthorizationVersionRecord record
                    => record.ResourceName == resourceName && record.ResourceKey == resourceKey,
                ResourcePermissionGrantRecord record
                    => record.ResourceName == resourceName && record.ResourceKey == resourceKey,
                _ => false
            });

        // 逐条恢复，避免 ChangeTracker.Clear() 丢弃宿主的其他待写实体。
        void DiscardPendingChanges()
        {
            foreach (var entry in touched)
            {
                entry.State = entry.State switch
                {
                    EntityState.Added => EntityState.Detached,
                    EntityState.Deleted => EntityState.Unchanged,
                    EntityState.Modified => EntityState.Unchanged,
                    _ => entry.State
                };
            }

            dbContext.Entry(version!).State = EntityState.Detached;
        }

        async Task<ResourceGrantConcurrencyException> ConflictAsync()
        {
            var actualVersion = await dbContext.Set<ResourceAuthorizationVersionRecord>()
                .AsNoTracking()
                .Where(x => x.ResourceName == resourceName && x.ResourceKey == resourceKey)
                .Select(x => x.Version)
                .FirstOrDefaultAsync(cancellationToken);

            return new ResourceGrantConcurrencyException(
                resourceName,
                resourceKey,
                expectedVersion ?? currentVersion,
                actualVersion);
        }
    }

    /// <inheritdoc />
    public async Task<int> RemoveResourceAsync(
        string resourceName,
        string resourceKey,
        CancellationToken cancellationToken = default)
    {
        // 一次提交同时删除授予行和版本行，避免并发写入留下无版本 ACL。
        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var grants = await dbContext.Set<ResourcePermissionGrantRecord>()
            .Where(x => x.ResourceName == resourceName && x.ResourceKey == resourceKey)
            .ToListAsync(cancellationToken);

        var versions = await dbContext.Set<ResourceAuthorizationVersionRecord>()
            .Where(x => x.ResourceName == resourceName && x.ResourceKey == resourceKey)
            .ToListAsync(cancellationToken);

        if (grants.Count == 0 && versions.Count == 0)
            return 0;

        dbContext.RemoveRange(grants);
        dbContext.RemoveRange(versions);
        await dbContext.SaveChangesAsync(cancellationToken);

        return grants.Count;
    }

    /// <inheritdoc />
    public async Task<int> RemoveProviderAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var grants = await dbContext.Set<ResourcePermissionGrantRecord>()
            .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);

        if (grants.Count == 0)
            return 0;

        // 保留并推进每个资源的版本，防止旧编辑者恢复刚删除的主体授予。
        // 按资源名分组批量查询，避免 N+1 和跨资源类型误匹配同名 Key。
        var affectedByResourceName = grants
            .GroupBy(x => x.ResourceName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.ResourceKey).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        foreach (var (resourceName, resourceKeys) in affectedByResourceName)
        {
            var versions = await dbContext.Set<ResourceAuthorizationVersionRecord>()
                .Where(x => x.ResourceName == resourceName && resourceKeys.Contains(x.ResourceKey))
                .ToListAsync(cancellationToken);

            foreach (var version in versions)
            {
                version.Version++;
            }
        }

        dbContext.RemoveRange(grants);
        await dbContext.SaveChangesAsync(cancellationToken);

        return grants.Count;
    }
}
