using Leistd.EventBus.Local;
using System.Reflection;
using Leistd.Auditing.EntityFrameworkCore;
using Leistd.Auditing.EntityFrameworkCore.Interceptors;
using Leistd.Data;
using Leistd.Data.EntityFrameworkCore;
using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Ddd.Infrastructure.EventBus;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.Ddd.Infrastructure.Persistence.Interceptors;
using Leistd.Ddd.Infrastructure.HostedServices;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.Options;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Leistd.Ddd.Infrastructure.Persistence.Repositories;
using Leistd.Timing;
using Leistd.DependencyInjection.Extensions;

namespace Leistd.Ddd.Infrastructure;

/// <summary>DDD 基础设施服务注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册工作单元、本地事件总线、数据过滤和 DbContext 仓储等 DDD 基础设施。</summary>
    /// <remarks>
    /// 可重复调用：服务只注册一次，<paramref name="configureUnitOfWork"/> 每次都叠加；工作单元配置节的规则同 <c>AddUnitOfWork</c>。
    /// </remarks>
    /// <example>
    /// <code>
    /// // 必要步骤：拦截器织入与漏登记校验都由这个工厂驱动，不装则两者都不生效。
    /// builder.Host.UseServiceProviderFactory(new DynamicProxyServiceRegistrationCallbackFactory());
    ///
    /// builder.Services.AddDddInfrastructure();
    /// builder.Services.AddDbContext&lt;AppDbContext&gt;(options =&gt; options.UseNpgsql(connectionString));
    ///
    /// // 登记上下文、挂载保存拦截器并注册仓储
    /// builder.Services.AddDddDbContext&lt;AppDbContext&gt;(o =&gt; o.AddDefaultRepositories());
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configureUnitOfWork">工作单元的编程式配置，在 <c>Leistd:UnitOfWork</c> 配置节绑定之后应用。</param>
    public static IServiceCollection AddDddInfrastructure(
        this IServiceCollection services,
        Action<UnitOfWorkOptions>? configureUnitOfWork = null)
    {
        services.AddDataFilters();
        services.AddDataEfCore();

        // 保留宿主或测试预先注册的时钟实现。
        services.TryAddSingleton<IClock, UtcClockProvider>();

        services.AddAuditingEfCore();

        // 领域事件由保存拦截器收集后发布，事件总线由基座注册
        services.AddLocalEventBus();
        // 待发布事件按 DbContext 暂存在静态弱表中，拦截器本身无状态。
        services.TryAddTransient<LocalEventSaveChangesInterceptor>();

        services.TryAddSingleton<ConcurrencyStampSaveChangesInterceptor>();

        services.AddUnitOfWork(configureUnitOfWork);

        services.AddUnitOfWorkEfCore();

        // 上下文清单由 AddDddDbContext<TDbContext>() 显式登记。
        GetOrCreateTrackedDbContextTypes(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, MultiTenantFilterGuard>());

        // 通过 Leistd 服务提供器工厂校验 DbContext 是否全部显式登记，避免遗漏租户过滤器检查。
        services.AddRegistrationValidator(EnsureEveryDbContextIsDeclared);

        return services;
    }

    /// <summary>把一个 DbContext 接入 DDD 基础设施：登记进租户过滤器闸门，挂载保存拦截器，并按选项注册仓储。</summary>
    /// <remarks>
    /// <para>派生自 <see cref="BaseDbContext"/> 的上下文经 <c>ConfigureDbContext&lt;TDbContext&gt;</c> 挂载三个保存拦截器：
    /// 修改/删除审计（含软删除转换）、领域事件收集与发布、并发标记换发；与 <c>AddDbContext</c> 的先后无关。
    /// 其他上下文不挂载，需要审计时在 <c>AddDbContext</c> 中自行添加 <c>AuditSaveChangesInterceptor</c>。
    /// 拦截器按作用域解析，因此不支持 DbContext 池与默认（单例）生命周期的 <c>AddDbContextFactory</c>：
    /// 其选项为单例，解析拦截器与 <see cref="BaseDbContext"/> 所用的都是根容器。</para>
    /// <para>每个已注册 DbContext 都须登记，包括不需要仓储的上下文。
    /// 宿主必须使用 Leistd 服务提供器工厂，才能在构建容器时检测漏登记。
    /// 省略选项时仅登记上下文，不注册仓储。</para>
    /// <para>同一上下文重复调用只登记一次、保存拦截器只挂一份；为已有仓储的实体再次登记相同实现时不重复注册，
    /// 登记不同实现（含另一个上下文的默认仓储）抛出 <see cref="InvalidOperationException"/>。</para>
    /// <para>仓储实现另外实现的、派生自 <c>IRepository&lt;TEntity&gt;</c> 的自定义接口一并按 Scoped 注册，
    /// 重复与冲突规则同上。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // 业务上下文：要仓储
    /// builder.Services.AddDddDbContext&lt;AppDbContext&gt;(o =&gt; o.AddDefaultRepositories());
    ///
    /// // 控制面上下文：只登记
    /// builder.Services.AddDddDbContext&lt;ControlPlaneDbContext&gt;();
    /// </code>
    /// </example>
    /// <typeparam name="TDbContext">已通过 <c>AddDbContext</c> 注册的上下文类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">仓储注册选项；省略时只登记上下文。</param>
    public static IServiceCollection AddDddDbContext<TDbContext>(
        this IServiceCollection services,
        Action<DddDbContextOptions>? configure = null)
        where TDbContext : DbContext
    {
        var options = new DddDbContextOptions();
        configure?.Invoke(options);

        var firstRegistration = GetOrCreateTrackedDbContextTypes(services).Add(typeof(TDbContext));

        // 保存拦截器只挂基座上下文：控制面等普通上下文不带租户与领域事件语义，按需自行挂审计。
        // 同一上下文重复登记时不再挂一遍，否则每次保存会审计、发布两次。
        if (firstRegistration && typeof(BaseDbContext).IsAssignableFrom(typeof(TDbContext)))
        {
            services.ConfigureDbContext<TDbContext>((sp, dbContextOptions) => dbContextOptions.AddInterceptors(
                sp.GetRequiredService<AuditSaveChangesInterceptor>(),
                sp.GetRequiredService<LocalEventSaveChangesInterceptor>(),
                sp.GetRequiredService<ConcurrencyStampSaveChangesInterceptor>()));
        }

        RegisterRepositories(services, typeof(TDbContext), options);

        return services;
    }

    // 重复注册时复用同一清单，避免不同实例各自只收集部分上下文。
    private static TrackedDbContextTypes GetOrCreateTrackedDbContextTypes(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(TrackedDbContextTypes))
                ?.ImplementationInstance is TrackedDbContextTypes existing)
        {
            return existing;
        }

        var tracked = new TrackedDbContextTypes();
        services.AddSingleton(tracked);
        return tracked;
    }

    // 本上下文按 DbSet<T> 声明派生的聚合根。注册阶段拿不到 EF Core 模型
    // （取 Model 要实例化 DbContext，而那需要已构建的容器），只能反射类型。
    private static IEnumerable<Type> DiscoverEntityTypes(Type dbContextType) =>
        dbContextType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Where(t => typeof(IAggregateRoot).IsAssignableFrom(t));

    // 先把「实体 → 实现」定案再落注册：同一上下文内 AddDefaultRepositories 与
    // AddDefaultRepository 重叠时自然去重，且自定义实现恒优先。
    private static Dictionary<Type, Type> ResolveRepositoryPlan(
        Type dbContextType,
        DddDbContextOptions options)
    {
        var plan = new Dictionary<Type, Type>(options.CustomRepositories);

        var defaults = options.RegisterDefaultRepositories
            ? DiscoverEntityTypes(dbContextType).Concat(options.ExplicitEntities)
            : options.ExplicitEntities;

        foreach (var entityType in defaults)
        {
            if (plan.ContainsKey(entityType))
            {
                continue;
            }

            var keyType = FindPrimaryKeyType(entityType);
            plan[entityType] = keyType is null
                ? typeof(EfCoreRepository<,>).MakeGenericType(dbContextType, entityType)
                : typeof(EfCoreRepository<,,>).MakeGenericType(dbContextType, entityType, keyType);
        }

        return plan;
    }

    private static void RegisterRepositories(
        IServiceCollection services,
        Type dbContextType,
        DddDbContextOptions options)
    {
        foreach (var (entityType, implementationType) in ResolveRepositoryPlan(dbContextType, options))
        {
            AddRepositoryDescriptor(services, typeof(IRepository<>).MakeGenericType(entityType), implementationType, entityType);

            if (FindPrimaryKeyType(entityType) is { } keyType)
            {
                var keyedInterface = typeof(IRepository<,>).MakeGenericType(entityType, keyType);

                // 带主键实体还会注册 IRepository<TEntity,TKey>，须同时验证自定义仓储实现该接口。
                if (!keyedInterface.IsAssignableFrom(implementationType))
                {
                    throw new InvalidOperationException(
                        $"Repository '{implementationType.FullName}' registered for entity " +
                        $"'{entityType.FullName}' does not implement " +
                        $"'IRepository<{entityType.Name}, {keyType.Name}>'. Entities with a primary key " +
                        "are registered under both the keyed and the non-keyed repository interface, so " +
                        "the implementation must provide both.");
                }

                AddRepositoryDescriptor(services, keyedInterface, implementationType, entityType);
            }

            // 自定义仓储接口（如 IUserRepository : IRepository<User, Guid>）与默认接口同属一个实现，
            // 按同样的生命周期与唯一性规则注册，业务代码才能直接注入它。
            var entityRepository = typeof(IRepository<>).MakeGenericType(entityType);
            foreach (var customInterface in implementationType.GetInterfaces())
            {
                if (customInterface != entityRepository &&
                    !(customInterface.IsGenericType &&
                      customInterface.GetGenericTypeDefinition() == typeof(IRepository<,>)) &&
                    entityRepository.IsAssignableFrom(customInterface))
                {
                    AddRepositoryDescriptor(services, customInterface, implementationType, entityType);
                }
            }
        }
    }

    // 同一实体被两个上下文各注册一次时 Microsoft DI 会让后注册者静默胜出，此处直接拒绝；相同实现的重复登记幂等跳过。
    private static void AddRepositoryDescriptor(
        IServiceCollection services,
        Type repositoryInterface,
        Type implementationType,
        Type entityType)
    {
        if (services.FirstOrDefault(d => d.ServiceType == repositoryInterface) is { } existing)
        {
            if (existing.ImplementationType == implementationType && existing.Lifetime == ServiceLifetime.Scoped)
            {
                return;
            }

            throw new InvalidOperationException(
                $"A repository for '{entityType.FullName}' is already registered as " +
                $"'{existing.ImplementationType?.FullName ?? "<factory>"}'. Registering " +
                $"'{implementationType.FullName}' would silently win by ordering and the caller " +
                "could not tell which database it reads. Map the entity in one DbContext, or use " +
                $"{nameof(DddDbContextOptions.AddRepository)} to name the implementation explicitly.");
        }

        services.AddScoped(repositoryInterface, implementationType);
    }

    // 注册了 DbContext 却没有显式接入的，宿主启动即失败：那个上下文会绕过租户过滤器闸门。
    // 不要仓储的上下文调用无参重载即可。
    private static void EnsureEveryDbContextIsDeclared(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(TrackedDbContextTypes))
                ?.ImplementationInstance is not TrackedDbContextTypes tracked)
        {
            return;
        }

        var undeclared = services
            .Select(d => d.ServiceType)
            .Where(t => typeof(DbContext).IsAssignableFrom(t) && t != typeof(DbContext) && !t.IsAbstract)
            .Distinct()
            .Where(t => !tracked.Types.Contains(t))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (undeclared.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"These DbContext types are registered but never passed to AddDddDbContext<TDbContext>(): " +
            $"{string.Join(", ", undeclared)}. They would escape the multi-tenant filter guard, which " +
            "is what stops multi-tenant entities from being mapped into a context with no tenant filter. " +
            "Call AddDddDbContext<TDbContext>() for each of them; pass no options when the context only " +
            "needs the unit of work and the guard.");
    }

    private static Type? FindPrimaryKeyType(Type entityType)
    {
        var entityInterface = entityType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                                 i.GetGenericTypeDefinition() == typeof(IEntity<>));
        return entityInterface?.GetGenericArguments()[0];
    }
}
