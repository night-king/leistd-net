using Leistd.Data.Filters;
using Leistd.Ddd.Infrastructure.Persistence.Conventions;
using Leistd.Data.EntityFrameworkCore.Modeling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Auditing.EntityFrameworkCore.Extensions;
using Leistd.Auditing.EntityFrameworkCore.Conventions;
using Leistd.Auditing.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Ddd.Infrastructure.Persistence;

/// <summary>提供审计、租户隔离、软删除和领域事件集成的 DbContext 基类。</summary>
/// <remarks>
/// 提供三件事：新增实体的环境值（<c>TenantId</c> / 创建审计）在进入跟踪时落定、
/// 软删除与租户隔离的全局查询过滤器、领域事件的收集。
/// <c>OnModelCreating</c> 与 <c>ConfigureConventions</c> 被封闭，派生类改覆盖 <c>ConfigureModel</c> 与
/// <c>ConfigureModelConventions</c>，且无需调 <c>base</c>。审计字段与并发标记分别由 <see cref="AuditingEntityConvention"/> 与 <see cref="DddEntityConvention"/> 配置。
/// 修改/删除审计、领域事件发布与并发标记换发依赖 SaveChanges 拦截器，由 <c>AddDddDbContext&lt;TDbContext&gt;()</c> 挂载。
/// </remarks>
public abstract class BaseDbContext : DbContext
{
    /// <summary>软删除查询过滤器名。</summary>
    public const string SoftDeleteFilterName = "SoftDelete";

    /// <summary>租户隔离查询过滤器名。</summary>
    public const string MultiTenantFilterName = "MultiTenant";

    private readonly IServiceProvider? _serviceProvider;

    private IAuditPropertySetter? _auditPropertySetter;
    private bool _auditPropertySetterResolved;

    private IDataFilter? _dataFilter;
    private bool _dataFilterResolved;

    private ICurrentTenant? _currentTenant;
    private bool _currentTenantResolved;

    /// <summary>创建审计设置器；未注册审计组件时为 <see langword="null"/>。</summary>
    protected virtual IAuditPropertySetter? AuditPropertySetter
    {
        get
        {
            if (!_auditPropertySetterResolved)
            {
                _auditPropertySetter = _serviceProvider?.GetService<IAuditPropertySetter>();
                _auditPropertySetterResolved = true;
            }

            return _auditPropertySetter;
        }
    }

    // 查询过滤器会频繁读取此服务；缓存实例不会固化其 AsyncLocal 状态。
    private IDataFilter? DataFilter
    {
        get
        {
            if (!_dataFilterResolved)
            {
                _dataFilter = _serviceProvider?.GetService<IDataFilter>();
                _dataFilterResolved = true;
            }

            return _dataFilter;
        }
    }

    // ICurrentTenant 按读取时访问 AsyncLocal，缓存实例不会固化租户。
    private ICurrentTenant? CurrentTenant
    {
        get
        {
            if (!_currentTenantResolved)
            {
                _currentTenant = _serviceProvider?.GetService<ICurrentTenant>();
                _currentTenantResolved = true;
            }

            return _currentTenant;
        }
    }

    // 不可覆写：这两个开关只投影 IDataFilter 的运行期状态，写入侧的 TenantId 落值不受其影响；
    // 覆写为 false 会得到“读不过滤、写照样落租户”。整个上下文不参与多租户时，不映射 IMultiTenant 实体。
    private bool IsSoftDeleteFilterEnabled => DataFilter?.IsEnabled<ISoftDelete>() ?? true;

    private bool IsMultiTenantFilterEnabled => DataFilter?.IsEnabled<IMultiTenant>() ?? true;

    /// <summary>当前租户标识；<see langword="null"/> 表示宿主视角。</summary>
    /// <remarks>
    /// 设计时或未注册 <c>ICurrentTenant</c> 时为 <see langword="null"/>，租户过滤器表现为宿主视角。
    /// </remarks>
    protected virtual Guid? CurrentTenantId => CurrentTenant?.Id;

    /// <summary>创建上下文并接入容器，使 <c>IDataFilter</c> 的运行时开关、当前租户与审计原语可用。</summary>
    /// <param name="options">EF Core 上下文选项。</param>
    /// <param name="serviceProvider">
    /// 作用域容器。仅设计时工具与迁移传 <see langword="null"/>：此时没有当前租户与用户，
    /// 租户过滤器按宿主视角、全局过滤器固定启用。运行时传 <see langword="null"/> 会让租户用户看到宿主数据。
    /// </param>
    protected BaseDbContext(
        DbContextOptions options,
        IServiceProvider? serviceProvider) : base(options)
    {
        _serviceProvider = serviceProvider;
        SubscribeTrackingHooks();
    }

    private void SubscribeTrackingHooks()
    {
        ChangeTracker.OnEntityEnteringAdded(ApplyConceptsForAddedEntity);
    }

    // 进入跟踪时落定租户与创建者，避免延迟保存跨越上下文边界。
    private void ApplyConceptsForAddedEntity(EntityEntry entry)
    {
        SetTenantId(entry);
        AuditPropertySetter?.SetCreationProperties(entry);
    }

    private void SetTenantId(EntityEntry entry)
    {
        if (entry.Entity is not IMultiTenant { TenantId: null })
        {
            return;
        }

        if (CurrentTenantId is { } tenantId)
        {
            entry.Property(nameof(IMultiTenant.TenantId)).CurrentValue = tenantId;
        }
    }

    /// <summary>构建模型并在派生配置后应用全局过滤器。</summary>
    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureModel(modelBuilder);

        modelBuilder.ApplyGlobalFilters<ISoftDelete>(SoftDeleteFilterName, e =>
            !IsSoftDeleteFilterEnabled ||
            !EF.Property<bool>(e, nameof(ISoftDelete.IsDeleted)));

        modelBuilder.ApplyGlobalFilters<IMultiTenant>(MultiTenantFilterName, e =>
            !IsMultiTenantFilterEnabled ||
            EF.Property<Guid?>(e, nameof(IMultiTenant.TenantId)) == CurrentTenantId);
    }

    /// <summary>注册审计与并发标记约定，再交由 <see cref="ConfigureModelConventions"/> 追加宿主约定。</summary>
    protected sealed override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new AuditingEntityConvention());
        configurationBuilder.Conventions.Add(_ => new DddEntityConvention());
        ConfigureModelConventions(configurationBuilder);
    }

    /// <summary>配置模型约定（如枚举统一存为字符串）；基类的实体约定已先行注册。</summary>
    protected virtual void ConfigureModelConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <summary>配置实体映射；基类会在此后应用全局过滤器。</summary>
    protected virtual void ConfigureModel(ModelBuilder modelBuilder)
    {
    }
}
