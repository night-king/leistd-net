using System.Globalization;
using Leistd.EventBus.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.Events;
using Leistd.Settings.Exceptions;
using Leistd.Settings.Validation;
using Leistd.Settings.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.Management;

/// <summary>写入前对照定义校验设置名、层级与取值，写入后发布 <see cref="SettingChangedEvent"/>。</summary>
/// <remarks>
/// <para>取值校验依次是：空串拒绝、<see cref="ISettingDefinition.ValueType"/> 与区间、
/// <see cref="ISettingDefinition.AllowedValues"/>、宿主注册的 <see cref="ISettingValueValidator"/>。
/// 清除（值为 <see langword="null"/>）不做值元数据校验，但以清除后会生效的值（下一层或代码默认值）经过业务校验器，
/// 跨设置的约束因此不会被一次清除绕过；清除后没有任何值时不调用校验器。</para>
/// <para>写入后作废同一作用域 <see cref="ISettingProvider"/> 的记忆化结果，同一请求里先写后读能读到新值。</para>
/// </remarks>
/// <param name="definitionManager">设置定义。</param>
/// <param name="store">设置值存储。</param>
/// <param name="settingProvider">同一作用域的设置读取器，写入后作废它的记忆化结果。</param>
/// <param name="validators">业务取值校验器。</param>
/// <param name="eventBus">发布变更事件；未注册本地事件总线时不发布。</param>
/// <param name="dataProtectionProvider">机密设置的加解密用宿主的 Data Protection；为 <see langword="null"/> 时写入机密设置、
/// 以及清除用户覆盖而需要解密租户层回落值时会失败。</param>
/// <param name="localizerFactory">校验失败时按请求语言取设置显示名；未启用本地化时用定义上的显示名。</param>
/// <param name="managementOptions">提供显示名词条所在的资源类型。</param>
public sealed class DefaultSettingManager(
    ISettingDefinitionManager definitionManager,
    ISettingStore store,
    ISettingProvider settingProvider,
    IEnumerable<ISettingValueValidator> validators,
    ILocalEventBus? eventBus = null,
    IDataProtectionProvider? dataProtectionProvider = null,
    IStringLocalizerFactory? localizerFactory = null,
    IOptions<SettingManagementOptions>? managementOptions = null) : ISettingManager
{
    // 与读取器同一用途，写入的密文它才解得开
    private readonly IDataProtector? _protector =
        dataProtectionProvider?.CreateProtector(DefaultSettingProvider.ProtectionPurpose);

    /// <inheritdoc />
    public async Task SetAsync(
        string name,
        string? value,
        SettingScopes scope,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var definition = definitionManager.GetOrNull(name) ?? throw new UndefinedSettingException(name);

        // 只有这三个能落到某一行：None 不是层级，All 是定义侧的“两层都允许”。
        if (scope is not (SettingScopes.Tenant or SettingScopes.User or SettingScopes.Host)
            || !definition.Scopes.HasFlag(scope))
        {
            throw new SettingScopeNotAllowedException(name, scope, definition.Scopes, DisplayName(definition));
        }

        if (scope == SettingScopes.User)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        }
        else
        {
            userId = null;
        }

        if (value is not null)
        {
            EnsureWellFormed(definition, value);
        }

        // 回落值来自定义或下一层，不再跑值元数据校验；业务规则（常常牵涉另一项设置）仍按它判定
        var candidate = value ?? await FallbackAfterClearAsync(definition, scope, cancellationToken);
        if (candidate is not null)
        {
            var context = new SettingValueValidationContext(definition, candidate, scope, userId);
            foreach (var validator in validators)
            {
                await validator.ValidateAsync(context, cancellationToken);
            }
        }

        var stored = definition.IsEncrypted && value is not null
            ? (_protector ?? throw DefaultSettingProvider.MissingDataProtection(name))
                .CreateProtector(name)
                .Protect(value)
            : value;

        await store.SetAsync(name, stored, scope, userId, cancellationToken);
        (settingProvider as DefaultSettingProvider)?.Invalidate();

        if (eventBus is not null)
        {
            await eventBus.PublishAsync(new SettingChangedEvent(name, scope, userId), cancellationToken);
        }
    }

    // 移除本层覆盖后会生效的明文值：用户层回落到租户层存储值（定义允许时），再回落到代码默认值；
    // 租户层与宿主层回落到代码默认值（被绑定的宿主级设置即部署基线）。直接读存储而不是 ISettingProvider，
    // 后者会返回本层尚未清除的覆盖值。
    private async Task<string?> FallbackAfterClearAsync(
        ISettingDefinition definition,
        SettingScopes scope,
        CancellationToken cancellationToken)
    {
        if (scope == SettingScopes.User && definition.Scopes.HasFlag(SettingScopes.Tenant))
        {
            var tenantValues = await store.GetAllAsync(SettingScopes.Tenant, userId: null, cancellationToken);
            if (tenantValues.TryGetValue(definition.Name, out var tenantValue))
            {
                return definition.IsEncrypted
                    ? DefaultSettingProvider.Decrypt(_protector, definition.Name, tenantValue)
                    : tenantValue;
            }
        }

        return definition.DefaultValue;
    }

    // 报错用界面上的显示名，只在失败路径上按当前请求语言解析
    private string DisplayName(ISettingDefinition definition)
        => SettingDisplayNames.Resolve(
            definition,
            SettingDisplayNames.CreateLocalizer(localizerFactory, managementOptions?.Value));

    // 只有 null 表示清除；空串会挡住回落、又被消费方当成未设置，因此拒绝
    private void EnsureWellFormed(ISettingDefinition definition, string value)
    {
        if (value.Length == 0)
        {
            var displayName = DisplayName(definition);
            throw new BusinessException(SettingErrorCodes.EmptyValueRejected,
                    $"An empty value is not accepted for '{displayName}'. Send null to clear the override.")
                .WithData("Name", displayName);
        }

        switch (definition.ValueType)
        {
            case SettingValueType.Boolean when value is not ("true" or "false"):
                throw new BusinessException(SettingErrorCodes.BooleanRequired, $"'{value}' is not a boolean; use 'true' or 'false'.")
                    .WithData("Value", value);

            case SettingValueType.Integer:
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    var displayName = DisplayName(definition);
                    throw new BusinessException(SettingErrorCodes.IntegerRequired, $"'{displayName}' must be an integer.")
                        .WithData("Name", displayName);
                }

                if (number < (definition.Minimum ?? int.MinValue) || number > (definition.Maximum ?? int.MaxValue))
                {
                    var minimum = definition.Minimum ?? int.MinValue;
                    var maximum = definition.Maximum ?? int.MaxValue;
                    var displayName = DisplayName(definition);
                    throw new BusinessException(SettingErrorCodes.ValueOutOfRange, $"'{displayName}' must be an integer between {minimum} and {maximum}.")
                        .WithData("Name", displayName)
                        .WithData("Minimum", minimum)
                        .WithData("Maximum", maximum);
                }

                break;
        }

        if (definition.AllowedValues is { } allowed && !allowed.Contains(value, StringComparer.Ordinal))
        {
            var candidates = string.Join(", ", allowed);
            throw new BusinessException(SettingErrorCodes.ValueNotAllowed, $"'{value}' is not an allowed value. Allowed: {candidates}.")
                .WithData("Value", value)
                .WithData("Allowed", candidates);
        }
    }
}
