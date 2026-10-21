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

        var names = definitionManager.GetAll()
            .Select(definition => definition.Name)
            .Where(name => definitionManager.IsAvailableOn(name, side))
            .ToList();

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
}
