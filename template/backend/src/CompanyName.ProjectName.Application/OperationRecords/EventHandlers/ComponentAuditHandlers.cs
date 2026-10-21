using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Events;
using Leistd.EventBus.EventHandlers;
#if (LocalIdentity && IncludeMultiTenancy)
using Leistd.MultiTenancy.Management.Events;
#endif
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Settings.Definitions;
using Leistd.Settings.Events;

namespace CompanyName.ProjectName.Application.OperationRecords.EventHandlers;

/// <summary>设置变更留痕：租户层与宿主层的写入记一条，个人偏好不记。</summary>
/// <remarks>
/// <para>目标标识带出作用域：同一个设置名在宿主与租户两层各有一行，只记名字会让两层的变更在审计里长得一模一样。</para>
/// <para>个人偏好刻意不记：不改变能力边界也不改变共享数据，却是高频写入——审计表的价值来自密度。</para>
/// <para>在写入提交之后执行（本地事件处理器的默认阶段），回滚了的写入不会留下"记了但没发生"的记录。</para>
/// </remarks>
internal sealed class SettingChangedAuditHandler(
    IOperationRecorder recorder,
    ISettingDefinitionManager definitions) : IEventHandler<SettingChangedEvent>
{
    public Task HandleAsync(SettingChangedEvent @event, CancellationToken cancellationToken = default)
        => @event.Scope == SettingScopes.User
            ? Task.CompletedTask
            : recorder.RecordSucceededAsync(
                OperationRecordActions.SettingChanged,
                OperationTarget.For($"{@event.Scope}/{@event.Name}", @event.Name),
                // 凭什么：该设置所在分组的管理子权限；未登记分组的只要求父权限
                definitions.GetOrNull(@event.Name)?.Group is { } group
                && SettingGroupPermissions.All.TryGetValue(group, out var permission)
                    ? permission
                    : PermissionConstant.Settings.Default,
                cancellationToken);
}

/// <summary>权限授予整体替换留痕，是整套权限体系里最敏感的写操作之一。</summary>
/// <remarks>
/// 目标标识是 <c>{类型}/{Key}</c>，与端点上补记被拒尝试的元数据拼法逐字一致，按目标检索才查得全。
/// 授权依据按主体类型判定，不能写死成角色那个权限：给用户直授时那会在审计表里留下一条撒谎的"凭什么"。
/// </remarks>
internal sealed class PermissionGrantsReplacedAuditHandler(IOperationRecorder recorder) : IEventHandler<PermissionGrantsReplacedEvent>
{
    public Task HandleAsync(PermissionGrantsReplacedEvent @event, CancellationToken cancellationToken = default)
        => recorder.RecordSucceededAsync(
            OperationRecordActions.PermissionGrantsReplaced,
            OperationTarget.For($"{@event.ProviderName}/{@event.ProviderKey}", @event.SubjectDisplayName),
            @event.ProviderName == PermissionGrantProviderNames.User
                ? OperationRecordAuthorizations.DirectUserGrant
                : PermissionConstant.Roles.ManagePermissions,
            cancellationToken);
}
#if (LocalIdentity && IncludeMultiTenancy)

/// <summary>租户生命周期留痕。事件只在各步都已提交后发布：创建失败并已补偿时不会有"创建成功"的假账。</summary>
internal sealed class TenantChangedAuditHandler(IOperationRecorder recorder) : IEventHandler<TenantChangedEvent>
{
    public Task HandleAsync(TenantChangedEvent @event, CancellationToken cancellationToken = default)
    {
        var (action, basis) = @event.Change switch
        {
            TenantChangeKind.Created => (OperationRecordActions.TenantCreated, PermissionConstant.Tenants.Create),
            TenantChangeKind.Updated => (OperationRecordActions.TenantUpdated, PermissionConstant.Tenants.Update),
            // 启停是 Critical：它改变的是"这一整批人还能不能进来"
            TenantChangeKind.ActivationChanged => (OperationRecordActions.TenantActivationChanged, PermissionConstant.Tenants.Update),
            TenantChangeKind.Deleted => (OperationRecordActions.TenantDeleted, PermissionConstant.Tenants.Delete),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event.Change, "Unknown tenant change.")
        };

        return recorder.RecordSucceededAsync(action, OperationTarget.For(@event.TenantId, @event.DisplayName), basis, cancellationToken);
    }
}

/// <summary>租户连接登记的变化记成操作记录。</summary>
/// <remarks>
/// <para>连接决定租户数据落在哪个库，改动必须留痕。事件里没有连接串（它是凭据），
/// 记录只写租户与连接名；变化类型体现在动作码上，登记、改写、删除各一个。
/// 事件带的版本号没有记——操作记录没有放它的位置，而目前也没有按版本检索的需求，
/// 不为存一个版本号去扩展通用的成功记录模型。</para>
/// <para>目标标识是 <c>{租户标识}/{连接名}</c>，与设置、权限两个处理器的拼法一致：
/// 同一个租户的每个连接名各是一个可被改动的对象，只写租户标识会让它们在审计里混成一行。
/// 目标名是<b>租户显示名</b>而不是连接名——界面按目标名展示，写连接名会显示成
/// "为租户 default 登记了数据库连接"。</para>
/// </remarks>
internal sealed class TenantConnectionChangedAuditHandler(IOperationRecorder recorder)
    : IEventHandler<TenantConnectionChangedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(TenantConnectionChangedEvent @event, CancellationToken cancellationToken = default)
        => recorder.RecordSucceededAsync(
            ActionFor(@event.Change),
            OperationTarget.For($"{@event.TenantId}/{@event.Name}", @event.TenantDisplayName),
            PermissionConstant.Tenants.Update,
            cancellationToken);

    // 事件带着变化类型，记录也要带：合成一个码之后，事后分不出是新增落点、换库还是退回宿主库。
    // 三个分支都显式列出，不用 _ 兜底：组件将来给枚举加值时，兜底会把它静默归成"改写"，
    // 审计表里多出一批张冠李戴的记录，而且不报错。宁可在这里炸，逼人补一个动作码。
    private static string ActionFor(TenantConnectionChangeKind change) => change switch
    {
        TenantConnectionChangeKind.Registered => OperationRecordActions.TenantConnectionRegistered,
        TenantConnectionChangeKind.Changed => OperationRecordActions.TenantConnectionChanged,
        TenantConnectionChangeKind.Removed => OperationRecordActions.TenantConnectionRemoved,
        _ => throw new ArgumentOutOfRangeException(
            nameof(change), change, "This connection change kind has no operation action code yet.")
    };
}
#endif
