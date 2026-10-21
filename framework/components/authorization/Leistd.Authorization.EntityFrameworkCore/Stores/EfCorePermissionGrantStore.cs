using Microsoft.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Constants;
using Leistd.Authorization.EntityFrameworkCore.Entities;
using Leistd.Authorization.Exceptions;

namespace Leistd.Authorization.EntityFrameworkCore.Stores;

/// <summary>EF Core 权限授予存储。</summary>
/// <remarks>
/// 每个读取方法的数据库往返次数是常数。DbContext 经 <see cref="IDbContextProvider{TDbContext}"/> 获取：
/// 直接注入会让独立库租户的读取落到默认连接上。
/// </remarks>
public class EfCorePermissionGrantStore<TDbContext>(IDbContextProvider<TDbContext> dbContextProvider)
    : IPermissionGrantStore
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public async Task<PermissionGrantSet> GetGrantsAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(providerKey))
            return PermissionGrantSet.Empty(providerName, providerKey);

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        return await ReadStableAsync(
            providerName,
            providerKey,
            () => ReadVersionsAsync([providerKey], cancellationToken),
            async () =>
            {
                var grants = await dbContext.Set<PermissionGrantRecord>()
                    .AsNoTracking()
                    .Where(x => x.ProviderName == providerName && x.ProviderKey == providerKey)
                    .Select(x => x.PermissionName)
                    .ToListAsync(cancellationToken);

                return (IReadOnlyList<string>)grants;
            },
            (grants, versions) => new PermissionGrantSet(
                providerName,
                providerKey,
                grants,
                versions.GetValueOrDefault(providerKey)));

        Task<Dictionary<string, long>> ReadVersionsAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken token)
            => dbContext.Set<AuthorizationVersionRecord>()
                .AsNoTracking()
                .Where(x => x.ProviderName == providerName && keys.Contains(x.ProviderKey))
                .ToDictionaryAsync(x => x.ProviderKey, x => x.Version, StringComparer.Ordinal, token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionGrantSet>> GetGrantsAsync(
        string providerName,
        IReadOnlyCollection<string> providerKeys,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return [];

        var keys = providerKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0)
            return [];

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        return await ReadStableAsync<Dictionary<string, IReadOnlyList<string>>, IReadOnlyList<PermissionGrantSet>>(
            providerName,
            string.Join(",", keys),
            () => dbContext.Set<AuthorizationVersionRecord>()
                .AsNoTracking()
                .Where(x => x.ProviderName == providerName && keys.Contains(x.ProviderKey))
                .ToDictionaryAsync(x => x.ProviderKey, x => x.Version, StringComparer.Ordinal, cancellationToken),
            async () =>
            {
                var rows = await dbContext.Set<PermissionGrantRecord>()
                    .AsNoTracking()
                    .Where(x => x.ProviderName == providerName && keys.Contains(x.ProviderKey))
                    .Select(x => new { x.ProviderKey, x.PermissionName })
                    .ToListAsync(cancellationToken);

                return rows
                    .GroupBy(x => x.ProviderKey, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<string>)[.. group.Select(x => x.PermissionName)],
                        StringComparer.Ordinal);
            },
            (grantsByKey, versions) =>
            [
                .. keys.Select(key => new PermissionGrantSet(
                    providerName,
                    key,
                    grantsByKey.GetValueOrDefault(key) ?? [],
                    versions.GetValueOrDefault(key)))
            ]);
    }

    /// <inheritdoc />
    public async Task<SubjectPermissionGrants> GetGrantsForSubjectAsync(
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default)
    {
        var userKey = userId ?? string.Empty;
        var roleKeys = roleIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var dbContext = await dbContextProvider.GetDbContextAsync(cancellationToken);

        return await ReadStableAsync(
            PermissionGrantProviderNames.User,
            userKey,
            () => dbContext.Set<AuthorizationVersionRecord>()
                .AsNoTracking()
                .Where(x => (x.ProviderName == PermissionGrantProviderNames.User && x.ProviderKey == userKey)
                            || (x.ProviderName == PermissionGrantProviderNames.Role && roleKeys.Contains(x.ProviderKey)))
                .ToDictionaryAsync(x => x.ProviderName + "/" + x.ProviderKey, x => x.Version, StringComparer.Ordinal, cancellationToken),
            async () =>
            {
                var rows = await dbContext.Set<PermissionGrantRecord>()
                    .AsNoTracking()
                    .Where(x => (x.ProviderName == PermissionGrantProviderNames.User && x.ProviderKey == userKey)
                                || (x.ProviderName == PermissionGrantProviderNames.Role && roleKeys.Contains(x.ProviderKey)))
                    .Select(x => new { x.ProviderName, x.ProviderKey, x.PermissionName })
                    .ToListAsync(cancellationToken);

                return rows
                    .GroupBy(x => x.ProviderName + "/" + x.ProviderKey, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<string>)[.. group.Select(x => x.PermissionName)],
                        StringComparer.Ordinal);
            },
            (grantsByKey, versions) =>
            {
                PermissionGrantSet SetOf(string providerName, string providerKey)
                {
                    var key = providerName + "/" + providerKey;
                    return new PermissionGrantSet(
                        providerName,
                        providerKey,
                        grantsByKey.GetValueOrDefault(key) ?? [],
                        versions.GetValueOrDefault(key));
                }

                return new SubjectPermissionGrants(
                    SetOf(PermissionGrantProviderNames.User, userKey),
                    [.. roleKeys.Select(roleKey => SetOf(PermissionGrantProviderNames.Role, roleKey))]);
            });
    }

    // 读版本、读授予、再读版本；仅返回版本稳定的快照，避免把旧数据与新版本组合。
    private static async Task<TResult> ReadStableAsync<TData, TResult>(
        string providerName,
        string providerKey,
        Func<Task<Dictionary<string, long>>> readVersions,
        Func<Task<TData>> readData,
        Func<TData, Dictionary<string, long>, TResult> project)
    {
        // 同一主体连续 MaxAttempts 次写入才会耗尽重试，正常负载下第一次就稳定。
        const int MaxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            var before = await readVersions();
            var data = await readData();
            var after = await readVersions();

            if (SameVersions(before, after))
                return project(data, after);

            if (attempt >= MaxAttempts)
            {
                // 无法取得一致快照属于读取失败，不是调用方提交的版本冲突。
                throw new UnstableGrantSnapshotException($"{providerName}/{providerKey}", attempt);
            }
        }

        static bool SameVersions(Dictionary<string, long> before, Dictionary<string, long> after)
            => before.Count == after.Count
               && before.All(entry => after.TryGetValue(entry.Key, out var version) && version == entry.Value);
    }
}
