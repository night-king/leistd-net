using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Leistd.Authorization.EntityFrameworkCore.Entities;
using Leistd.Authorization.Exceptions;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;

namespace Leistd.Authorization.EntityFrameworkCore.Managers;

/// <summary>EF Core 权限授予管理器。</summary>
/// <remarks>
/// 所有写入经同一条归一化流水线：先校验权限已定义且启用（未定义抛 <see cref="UndefinedPermissionException"/>），
/// 再向上补齐全部祖先，因此单条授予、批量替换与种子数据结果一致。
/// 每个公开写方法以一次 <c>SaveChangesAsync</c> 提交；授权版本是并发令牌，落败方得到 <see cref="PermissionGrantConcurrencyException"/>。
/// DbContext 经 <see cref="IDbContextProvider{TDbContext}"/> 获取，宿主须已注册 <c>AddUnitOfWorkEfCore()</c>。
/// </remarks>
public class EfCorePermissionGrantManager<TDbContext>(
    IDbContextProvider<TDbContext> dbContextProvider,
    IPermissionDefinitionManager permissionDefinitionManager,
    IPermissionGrantStore permissionGrantStore) : IPermissionGrantManager
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public async Task<int> RemoveProviderAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(providerName, providerKey);

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        // 永久删除主体时同时清理授予行和版本行。
        var grants = await dbContext.Set<PermissionGrantRecord>()
            .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);

        var versions = await dbContext.Set<AuthorizationVersionRecord>()
            .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);

        if (grants.Count == 0 && versions.Count == 0)
            return 0;

        dbContext.RemoveRange(grants);
        dbContext.RemoveRange(versions);
        await dbContext.SaveChangesAsync(cancellationToken);

        return grants.Count;
    }

    /// <inheritdoc />
    public async Task GrantAsync(
        string permissionName,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(providerName, providerKey);
        ValidatePermissions([permissionName]);

        await MutateAsync(
            providerName,
            providerKey,
            desired => desired.Add(permissionName),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(
        string permissionName,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(providerName, providerKey);

        await MutateAsync(
            providerName,
            providerKey,
            desired =>
            {
                desired.Remove(permissionName);

                // 级联：父权限被撤销后，其子孙不再具备前置条件，一并清理。
                foreach (var descendant in permissionDefinitionManager.GetDescendantNames(permissionName))
                {
                    desired.Remove(descendant);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> ReplaceGrantsAsync(
        string providerName,
        string providerKey,
        IReadOnlyCollection<string> permissionNames,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(providerName, providerKey);
        ValidatePermissions(permissionNames);

        var desired = new HashSet<string>(permissionNames, StringComparer.Ordinal);

        return await ApplyAsync(providerName, providerKey, desired, expectedVersion, cancellationToken);
    }

    // 点操作基于带版本快照重试，避免静默覆盖并发改动。
    // 全量替换不重试，因为重放旧集合会造成丢失更新。
    private async Task MutateAsync(
        string providerName,
        string providerKey,
        Action<HashSet<string>> mutate,
        CancellationToken cancellationToken)
    {
        const int MaxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await permissionGrantStore.GetGrantsAsync(providerName, providerKey, cancellationToken);
            var desired = new HashSet<string>(snapshot.PermissionNames, StringComparer.Ordinal);
            mutate(desired);

            try
            {
                await ApplyAsync(providerName, providerKey, desired, snapshot.Version, cancellationToken);
                return;
            }
            catch (PermissionGrantConcurrencyException) when (attempt < MaxAttempts)
            {
                // 使用最新集合重做幂等点操作。
            }
        }
    }

    // 补齐父权限，使运行时可使用扁平集合判断。
    private HashSet<string> Normalize(HashSet<string> desired)
    {
        foreach (var name in desired.ToList())
        {
            desired.UnionWith(permissionDefinitionManager.GetAncestorNames(name));
        }

        return desired;
    }

    private async Task<long> ApplyAsync(
        string providerName,
        string providerKey,
        HashSet<string> desired,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var target = Normalize(desired);

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        var version = await dbContext.Set<AuthorizationVersionRecord>()
            .FirstOrDefaultAsync(
                x => x.ProviderName == providerName && x.ProviderKey == providerKey,
                cancellationToken);

        var currentVersion = version?.Version ?? 0;
        if (expectedVersion.HasValue && expectedVersion.Value != currentVersion)
        {
            throw new PermissionGrantConcurrencyException(
                providerName,
                providerKey,
                expectedVersion.Value,
                currentVersion);
        }

        var existing = await dbContext.Set<PermissionGrantRecord>()
            .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);

        var grantSet = dbContext.Set<PermissionGrantRecord>();

        // 精确记录本次改动，防止失败条目污染宿主后续的 SaveChanges。
        var touched = new List<EntityEntry<PermissionGrantRecord>>();

        foreach (var record in existing.Where(record => !target.Contains(record.PermissionName)))
        {
            touched.Add(grantSet.Remove(record));
        }

        var existingNames = existing
            .Select(x => x.PermissionName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var permissionName in target.Where(name => !existingNames.Contains(name)))
        {
            touched.Add(grantSet.Add(new PermissionGrantRecord
            {
                PermissionName = permissionName,
                ProviderName = providerName,
                ProviderKey = providerKey
            }));
        }

        if (touched.Count == 0)
            return currentVersion;

        var isFirstWrite = version == null;
        if (version == null)
        {
            version = new AuthorizationVersionRecord
            {
                ProviderName = providerName,
                ProviderKey = providerKey,
                Version = 1
            };
            await dbContext.Set<AuthorizationVersionRecord>().AddAsync(version, cancellationToken);
        }
        else
        {
            version.Version += 1;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex) when (InvolvesThisSubject(ex))
        {
            // 只映射涉及当前主体授权行的冲突，保留同次保存中其他实体的并发异常。
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

        // EF Entries 只包含并发令牌校验失败的条目。
        bool InvolvesThisSubject(DbUpdateConcurrencyException exception)
            => exception.Entries.Any(entry => entry.Entity switch
            {
                AuthorizationVersionRecord record
                    => record.ProviderName == providerName && record.ProviderKey == providerKey,
                PermissionGrantRecord record
                    => record.ProviderName == providerName && record.ProviderKey == providerKey,
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
                    _ => entry.State
                };
            }

            // 脱离过期版本实体，防止后续保存写回错误版本。
            dbContext.Entry(version).State = EntityState.Detached;
        }

        // 从存储重读并发赢家写入的实际版本。
        async Task<PermissionGrantConcurrencyException> ConflictAsync()
        {
            var actualVersion = await dbContext.Set<AuthorizationVersionRecord>()
                .AsNoTracking()
                .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
                .Select(x => x.Version)
                .FirstOrDefaultAsync(cancellationToken);

            return new PermissionGrantConcurrencyException(
                providerName,
                providerKey,
                expectedVersion ?? currentVersion,
                actualVersion);
        }
    }

    // 所有写入路径都在此拒绝未定义或已禁用的权限。
    private void ValidatePermissions(IEnumerable<string> permissionNames)
    {
        var unknown = permissionNames
            .Where(name => !permissionDefinitionManager.IsEffectivelyEnabled(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
            throw new UndefinedPermissionException(unknown);
    }

    // 公共 API 必须拒绝会形成孤儿授予的空白主体标识。
    private static void ValidateProvider(string providerName, string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
    }
}
