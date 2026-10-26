using Leistd.Authorization.Definitions;
using Leistd.Authorization.Exceptions;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.Grants;

internal sealed class PermissionGrantSeeder(
    IPermissionDefinitionManager definitionManager,
    IPermissionGrantStore grantStore,
    IPermissionGrantManager grantManager) : IPermissionGrantSeeder
{
    public async Task<int?> SeedAllAsync(
        string providerName,
        string providerKey,
        MultiTenancySides side,
        CancellationToken cancellationToken = default)
    {
        var existing = await grantStore.GetGrantsAsync(providerName, providerKey, cancellationToken);
        if (existing.Version != 0)
        {
            return null;
        }

        var names = AvailableNames(side);

        try
        {
            await grantManager.ReplaceGrantsAsync(providerName, providerKey, names, expectedVersion: 0, cancellationToken);
            return names.Count;
        }
        catch (PermissionGrantConcurrencyException)
        {
            // 另一个实例刚播种过
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> GetMissingAsync(
        string providerName,
        string providerKey,
        MultiTenancySides side,
        CancellationToken cancellationToken = default)
    {
        var existing = await grantStore.GetGrantsAsync(providerName, providerKey, cancellationToken);
        var granted = existing.PermissionNames.ToHashSet(StringComparer.Ordinal);
        return [.. AvailableNames(side).Where(name => !granted.Contains(name))];
    }

    public async Task<IReadOnlyList<string>> TopUpAsync(
        string providerName,
        string providerKey,
        MultiTenancySides side,
        CancellationToken cancellationToken = default)
    {
        var missing = await GetMissingAsync(providerName, providerKey, side, cancellationToken);

        // 点操作保留其余授予；整体替换会校验整个集合，已删除定义的旧名会让它拒绝写入
        foreach (var name in missing)
        {
            await grantManager.GrantAsync(name, providerName, providerKey, cancellationToken);
        }

        return missing;
    }

    private List<string> AvailableNames(MultiTenancySides side)
        => [.. definitionManager.GetAll()
            .Select(definition => definition.Name)
            .Where(name => definitionManager.IsAvailableOn(name, side))];
}
