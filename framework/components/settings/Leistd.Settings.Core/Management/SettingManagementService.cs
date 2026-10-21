using Leistd.ExceptionHandling;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Leistd.Settings.Stores;
using Leistd.Settings.Dtos;
using Leistd.Settings.Options;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.Management;

// 按层取原始覆盖值而不是 ISettingProvider 的回落结果，设置页才能分层编辑
internal sealed class SettingManagementService(
    ISettingStore settingStore,
    ISettingDefinitionManager definitionManager,
    ISettingManager settingManager,
    ICurrentUser currentUser,
    IOptions<SettingManagementOptions> options,
    IStringLocalizerFactory? localizerFactory = null) : ISettingManagementService
{
    public async Task<IReadOnlyList<SettingOutputDto>> GetAsync(CancellationToken cancellationToken = default)
    {
        var tenantValues = await settingStore.GetAllAsync(SettingScopes.Tenant, userId: null, cancellationToken);

        var isHost = settingStore.CanAccessHostScope;
        var definitions = definitionManager.GetAll()
            .Where(definition => definition.IsVisibleToClients)
            .Where(definition => isHost || !definition.Scopes.HasFlag(SettingScopes.Host))
            .ToList();

        var hostValues = isHost && definitions.Any(definition => definition.Scopes.HasFlag(SettingScopes.Host))
            ? await settingStore.GetAllAsync(SettingScopes.Host, userId: null, cancellationToken)
            : new Dictionary<string, string>();

        var userId = currentUser.Id?.ToString();
        var userValues = userId is null
            ? new Dictionary<string, string>()
            : await settingStore.GetAllAsync(SettingScopes.User, userId, cancellationToken);

        var localizer = SettingDisplayNames.CreateLocalizer(localizerFactory, options.Value);
        return [.. definitions.Select(definition => ToOutput(definition, userValues, tenantValues, hostValues, localizer))];
    }

    public async Task SetForCurrentUserAsync(SetSettingInputDto input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var userId = currentUser.Id?.ToString()
            ?? throw new BusinessException(SettingErrorCodes.IdentityCannotOperate, "Only a user can change personal settings.");

        _ = EnsureVisibleToClients(input.Name);

        await settingManager.SetAsync(input.Name, input.Value, SettingScopes.User, userId, cancellationToken);
    }

    public async Task SetForCurrentTenantAsync(SetSettingInputDto input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var definition = EnsureVisibleToClients(input.Name);

        // 进程级设置只有宿主那一行，租户上下文下由写入入口拒绝
        var scope = definition.Scopes.HasFlag(SettingScopes.Host) ? SettingScopes.Host : SettingScopes.Tenant;
        if (scope == SettingScopes.Host && !settingStore.CanAccessHostScope)
        {
            var displayName = SettingDisplayNames.Resolve(
                definition, SettingDisplayNames.CreateLocalizer(localizerFactory, options.Value));
            throw new BusinessException(SettingErrorCodes.HostOnly, $"Setting '{displayName}' is process-wide and can only be changed on the host.")
                .WithData("Name", displayName);
        }

        await settingManager.SetAsync(input.Name, input.Value, scope, cancellationToken: cancellationToken);
    }

    private SettingOutputDto ToOutput(
        ISettingDefinition definition,
        IReadOnlyDictionary<string, string> userValues,
        IReadOnlyDictionary<string, string> tenantValues,
        IReadOnlyDictionary<string, string> hostValues,
        IStringLocalizer? localizer)
    {
        var userValue = definition.Scopes.HasFlag(SettingScopes.User) ? userValues.GetValueOrDefault(definition.Name) : null;
        var tenantValue = definition.Scopes.HasFlag(SettingScopes.Host)
            ? hostValues.GetValueOrDefault(definition.Name)
            : definition.Scopes.HasFlag(SettingScopes.Tenant) ? tenantValues.GetValueOrDefault(definition.Name) : null;

        var group = string.IsNullOrWhiteSpace(definition.Group) ? options.Value.DefaultGroup : definition.Group;
        var secret = definition.IsEncrypted;

        return new SettingOutputDto(
            definition.Name,
            SettingDisplayNames.Resolve(definition, localizer),
            group,
            SettingDisplayNames.Localize(localizer, $"SettingGroup:{group}") ?? group,
            secret ? null : userValue,
            secret ? null : tenantValue,
            secret ? null : definition.DefaultValue,
            definition.Scopes.HasFlag(SettingScopes.Tenant),
            definition.Scopes.HasFlag(SettingScopes.User),
            definition.Scopes.HasFlag(SettingScopes.Host),
            definition.ValueType == SettingValueType.Integer ? definition.Minimum : null,
            definition.ValueType == SettingValueType.Integer ? definition.Maximum : null,
            definition.ValueType == SettingValueType.Boolean,
            secret,
            secret && (tenantValue ?? userValue) is not null,
            definition.AllowedValues);
    }

    private ISettingDefinition EnsureVisibleToClients(string name)
    {
        var definition = definitionManager.GetOrNull(name);
        if (definition is null || !definition.IsVisibleToClients)
        {
            throw new BusinessException(SettingErrorCodes.NotAvailable, $"Setting '{name}' is not available.")
                .WithData("Name", name);
        }

        return definition;
    }
}
