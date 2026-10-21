using System.Globalization;
using Leistd.EventBus.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.Events;
using Leistd.Settings.Exceptions;
using Leistd.Settings.Validation;
using Leistd.Settings.Options;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.Management;

/// <summary>写入前对照定义校验设置名、层级与取值，按租户串行地在独立事务里完成校验与写入，提交后分发 <see cref="SettingChangedEvent"/>。</summary>
/// <remarks>
/// <para>取值校验依次是：空串拒绝、<see cref="ISettingDefinition.ValueType"/> 与区间、
/// <see cref="ISettingDefinition.AllowedValues"/>、宿主注册的 <see cref="ISettingValueValidator"/>。
/// 清除（值为 <see langword="null"/>）不做值元数据校验，但以清除后会生效的值（下一层或代码默认值）经过业务校验器，
/// 跨设置的约束因此不会被一次清除绕过；清除后没有任何值时不调用校验器。</para>
/// <para>每次写入先按当前租户取 <see cref="IDistributedLock"/>（键 <c>settings:{租户 Id}</c>，宿主为 <c>settings:host</c>；
/// 用户层与租户层共用一把），再以 <c>requiresNew</c> 开一个事务型工作单元（隔离级别与超时沿用宿主的工作单元默认选项）。
/// 锁内依次：作废同一作用域 <see cref="ISettingProvider"/> 的记忆化结果 → 取值校验与业务校验器 → 存储写入 → 再次作废记忆化 →
/// 发布事件（进入本工作单元的提交后队列） → 提交；提交与提交后处理器全部结束、退出工作单元之后才释放锁。
/// 校验器经 <see cref="ISettingProvider"/> 读到的因此是锁内已提交的最新值，写入后同一作用域先写后读也读到新值。</para>
/// <para>锁获取失败或等待中取消原样上抛；持锁资格失效（<see cref="ILockHandle.LockLost"/>）与调用方取消合并，传给锁内的读写、
/// 校验器与提交前检查。提交开始之后沿用工作单元的不可逆边界，不再因取消报告未保存。</para>
/// </remarks>
/// <param name="definitionManager">设置定义。</param>
/// <param name="store">设置值存储；须经当前工作单元读写（参与其事务），见 <see cref="ISettingManager"/>。</param>
/// <param name="settingProvider">同一作用域的设置读取器，校验前与写入后作废它的记忆化结果。</param>
/// <param name="validators">业务取值校验器。</param>
/// <param name="distributedLock">按租户串行化写入；多副本部署须为跨进程实现（如 Redis）。</param>
/// <param name="unitOfWorkManager">为每次写入开独立的事务型工作单元。</param>
/// <param name="eventBus">发布变更事件；未注册本地事件总线时不发布。</param>
/// <param name="dataProtectionProvider">机密设置的加解密用宿主的 Data Protection；为 <see langword="null"/> 时写入机密设置、
/// 以及清除用户覆盖而需要解密租户层回落值时会失败。</param>
/// <param name="localizerFactory">校验失败时按请求语言取设置显示名；未启用本地化时用定义上的显示名。</param>
/// <param name="managementOptions">提供显示名词条所在的资源类型。</param>
/// <param name="currentTenant">锁键按它取租户；未注册多租户时全部写入共用宿主那把锁。</param>
/// <param name="unitOfWorkOptions">宿主的工作单元默认选项，提供隔离级别与超时；事务开关固定为开启。</param>
public sealed class DefaultSettingManager(
    ISettingDefinitionManager definitionManager,
    ISettingStore store,
    ISettingProvider settingProvider,
    IEnumerable<ISettingValueValidator> validators,
    IDistributedLock distributedLock,
    IUnitOfWorkManager unitOfWorkManager,
    ILocalEventBus? eventBus = null,
    IDataProtectionProvider? dataProtectionProvider = null,
    IStringLocalizerFactory? localizerFactory = null,
    IOptions<SettingManagementOptions>? managementOptions = null,
    ICurrentTenant? currentTenant = null,
    IOptions<UnitOfWorkOptions>? unitOfWorkOptions = null) : ISettingManager
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

        // 同租户的全部写入共用一把锁：用户层清除会回落到租户层值，跨设置规则也可能跨层读取
        await using var lease = await distributedLock.LockAsync(LockKey(), cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LockLost);

        // 锁要等提交之后才释放：requiresNew 不并入调用方的事务（并入的话要等外层结束才真正提交）
        using var unitOfWork = unitOfWorkManager.Begin(TransactionalOptions(), requiresNew: true);
        try
        {
            await WriteAsync(definition, value, scope, userId, linked.Token);
            await unitOfWork.CompleteAsync(linked.Token);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // 在锁与本次工作单元之内执行：校验器读到的是锁内的权威值，事件进入本次提交的提交后队列
    private async Task WriteAsync(
        ISettingDefinition definition,
        string? value,
        SettingScopes scope,
        string? userId,
        CancellationToken cancellationToken)
    {
        var reader = settingProvider as DefaultSettingProvider;

        // 本作用域之前读过的快照可能早于前一个持锁者的提交；校验器读另一项设置前必须丢掉
        reader?.Invalidate();

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
            ? (_protector ?? throw DefaultSettingProvider.MissingDataProtection(definition.Name))
                .CreateProtector(definition.Name)
                .Protect(value)
            : value;

        await store.SetAsync(definition.Name, stored, scope, userId, cancellationToken);

        // 校验器读取时又缓存了含本项旧值的快照；不再作废的话同一作用域写后读仍是旧值
        reader?.Invalidate();

        if (eventBus is not null)
        {
            await eventBus.PublishAsync(new SettingChangedEvent(definition.Name, scope, userId), cancellationToken);
        }
    }

    private string LockKey() => currentTenant?.Id is { } tenantId ? $"settings:{tenantId:D}" : "settings:host";

    // 宿主可能把默认工作单元配成非事务型；本操作的回滚与“提交先于释放锁”都依赖事务
    private UnitOfWorkOptions TransactionalOptions()
    {
        var options = unitOfWorkOptions?.Value.Clone() ?? new UnitOfWorkOptions();
        options.IsTransactional = true;
        return options;
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
