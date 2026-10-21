using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Entities;
using Leistd.DependencyInjection.Registration;
using Leistd.EventBus.Local;
using Xunit;
using Leistd.Auditing.Abstractions;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>租户过滤器就位闸门。</summary>
/// <remarks>
/// 各组件的 <c>Add*EfCore&lt;TDbContext&gt;()</c> 只能约束到 <c>where TDbContext : DbContext</c>
/// （components 不得依赖 ddd-struct），因此把普通 DbContext 传进去能编译通过，
/// 后果是读侧无租户谓词、写侧 TenantId 落不上，两者都静默。这组用例锁住那道启动期断言。
/// </remarks>
public class MultiTenantFilterGuardTests
{
    [Fact]
    public void Data_filters_are_valid_with_scope_validation_enabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalEventBus();
        services.AddDddInfrastructure();

        using var provider = (ServiceProvider)new ServiceRegistrationCallbackFactory(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true })
            .CreateServiceProvider(services);
        using var scope = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<IDataFilter<ISoftDelete>>(),
            scope.ServiceProvider.GetRequiredService<IDataFilter<ISoftDelete>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDataFilter>());
    }

    [Fact]
    public void Callback_factory_honors_service_provider_validation_options()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        services.AddSingleton<InvalidSingleton>();

        var factory = new ServiceRegistrationCallbackFactory(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Assert.Throws<AggregateException>(() => factory.CreateServiceProvider(services));
    }

    [Fact]
    public async Task Plain_dbcontext_mapping_multi_tenant_entity_fails_startup()
    {
        await using var provider = BuildProvider<PlainTenantDbContext>(withTenancy: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartHostedServicesAsync(provider));

        // 消息必须点名上下文与实体，否则运维只看到"启动失败"
        Assert.Contains(nameof(PlainTenantDbContext), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TenantScopedOrder), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(BaseDbContext), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaseDbContext_mapping_multi_tenant_entity_passes()
    {
        await using var provider = BuildProvider<GuardedTenantDbContext>(withTenancy: true);

        await StartHostedServicesAsync(provider);
    }

    [Fact]
    public async Task Owned_tenant_contracts_are_governed_by_the_owner_query_filter()
    {
        await using var provider = BuildProvider<OwnedTenantDbContext>(withTenancy: true);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnedTenantDbContext>();
        Assert.True(db.Model.FindEntityType(typeof(OwnedTenantDetails))!.IsOwned());
        await StartHostedServicesAsync(provider);
    }

    /// <summary>非多租户宿主零影响：没注册 ICurrentTenant 就不该有任何判定。</summary>
    /// <remarks>
    /// 这条同时防住"闸门变成新的启动失败来源"：
    /// 一个根本不分租户的服务不该因为实体恰好实现了标记接口就起不来。
    /// </remarks>
    [Fact]
    public async Task Host_without_current_tenant_is_not_checked()
    {
        await using var provider = BuildProvider<PlainTenantDbContext>(withTenancy: false);

        await StartHostedServicesAsync(provider);
    }

    /// <summary>上下文构造不出来时只跳过、不失败：闸门只回答"过滤器在不在"。</summary>
    [Fact]
    public async Task Uncreatable_dbcontext_is_skipped_instead_of_failing_startup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMultiTenancyCore();
        // 故意不注册 DbContextOptions：解析 UnregisteredDbContext 必然抛
        services.AddScoped<UnregisteredDbContext>();
        services.AddDddInfrastructure();
        services.AddDddDbContext<UnregisteredDbContext>();

        await using var provider = BuildFrom(services);
        await StartHostedServicesAsync(provider);
    }

    private static ServiceProvider BuildProvider<TDbContext>(bool withTenancy)
        where TDbContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        if (withTenancy)
        {
            services.AddMultiTenancyCore();
        }

        services.AddDbContext<TDbContext>(options =>
            options.UseInMemoryDatabase($"guard-{typeof(TDbContext).Name}-{Guid.NewGuid()}"));
        services.AddDddInfrastructure();
        services.AddDddDbContext<TDbContext>();

        return BuildFrom(services);
    }

    /// <summary>
    /// 按宿主的调用顺序经代理工厂构建：上下文清单由 <c>AddDddDbContext&lt;T&gt;()</c> 显式登记，
    /// 但"注册了却没登记"的校验器要靠工厂才会执行；工作单元的启动检查还要求工厂登记的织入标记
    /// </summary>
    private static ServiceProvider BuildFrom(IServiceCollection services)
    {
        var factory = new Leistd.DependencyInjection.DynamicProxy.Registration
            .DynamicProxyServiceRegistrationCallbackFactory();
        return (ServiceProvider)factory.CreateServiceProvider(factory.CreateBuilder(services));
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private class TenantScopedOrder : IMultiTenant
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; private set; }
    }

    /// <summary>普通 DbContext 映射了多租户实体——闸门要抓的形态</summary>
    private sealed class PlainTenantDbContext(DbContextOptions<PlainTenantDbContext> options)
        : DbContext(options)
    {
        public DbSet<TenantScopedOrder> Orders => Set<TenantScopedOrder>();
    }

    /// <summary>同样的实体，但经 BaseDbContext——过滤器与落值都在</summary>
    private sealed class GuardedTenantDbContext(DbContextOptions<GuardedTenantDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<TenantScopedOrder> Orders => Set<TenantScopedOrder>();
    }

    private sealed class OwnedTenantDbContext(DbContextOptions<OwnedTenantDbContext> options) : BaseDbContext(options, null)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<OwnedTenantOrder>().OwnsOne(order => order.Details);
    }

    private sealed class OwnedTenantOrder : IMultiTenant
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
        public OwnedTenantDetails Details { get; set; } = new();
    }

    private sealed class OwnedTenantDetails : IMultiTenant
    {
        public Guid? TenantId { get; set; }
    }

    /// <summary>真实体（实现 IEntity&lt;TKey&gt;），会进入仓储派生</summary>
    private sealed class SharedEntity : Leistd.Ddd.Domain.Entities.Entity<Guid>, IAggregateRoot<Guid>;

    /// <summary>只实现无主键契约的自定义仓储：能通过 AddRepository 的泛型约束</summary>
    private sealed class KeylessOnlyRepository(
        Leistd.UnitOfWork.EntityFrameworkCore.Database.IDbContextProvider<FirstRepoDbContext> dbContextProvider,
        Leistd.UnitOfWork.IUnitOfWorkManager uow)
        : Leistd.Ddd.Infrastructure.Persistence.Repositories.EfCoreRepository<FirstRepoDbContext, SharedEntity>(dbContextProvider, uow);

    /// <summary>映射 SharedEntity 的上下文之一</summary>
    private sealed class FirstRepoDbContext(DbContextOptions<FirstRepoDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<SharedEntity> Items => Set<SharedEntity>();
    }

    /// <summary>映射同一个 SharedEntity——用于验证跨上下文重复被拒</summary>
    private sealed class SecondRepoDbContext(DbContextOptions<SecondRepoDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<SharedEntity> Items => Set<SharedEntity>();
    }

    /// <summary>缺 DbContextOptions，解析必然抛</summary>
    private sealed class UnregisteredDbContext : DbContext
    {
        public DbSet<TenantScopedOrder> Orders => Set<TenantScopedOrder>();
    }

    private sealed class ScopedDependency;

    private sealed class InvalidSingleton(ScopedDependency dependency)
    {
        public ScopedDependency Dependency { get; } = dependency;
    }
    // 显式登记换来的新"忘写"模式必须响亮：注册了 DbContext 却没登记，
    // 那个上下文会逃出租户过滤器闸门。
    [Fact]
    public void Registered_but_undeclared_dbcontext_fails_container_build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<GuardedTenantDbContext>(o =>
            o.UseInMemoryDatabase($"undeclared-{Guid.NewGuid()}"));
        services.AddDddInfrastructure();
        // 刻意不调 AddDddDbContext<GuardedTenantDbContext>()

        var error = Assert.Throws<InvalidOperationException>(() => BuildFrom(services));

        Assert.Contains(nameof(GuardedTenantDbContext), error.Message);
        Assert.Contains("AddDddDbContext", error.Message);
    }

    // 同一实体被两个上下文各注册一次时，Microsoft DI 让后注册的静默胜出，
    // 调用方无从知道读的是哪个库。必须在注册时就拒绝。
    [Fact]
    public void Same_entity_in_two_contexts_is_rejected_at_registration()
    {
        var services = new ServiceCollection();
        services.AddDddDbContext<FirstRepoDbContext>(o => o.AddDefaultRepositories());

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddDddDbContext<SecondRepoDbContext>(o => o.AddDefaultRepositories()));

        Assert.Contains("already registered", error.Message);
        Assert.Contains(nameof(SharedEntity), error.Message);
    }

    // 带主键的实体会同时注册 IRepository<T> 与 IRepository<T,TKey>；自定义实现只满足前者时
    // 泛型约束拦不住（TKey 不在 AddRepository 的签名里），必须在注册期报清楚，
    // 而不是留到 ValidateOnBuild 抛一句"实现类型不可赋值"。
    [Fact]
    public void Custom_repository_missing_the_keyed_interface_is_rejected_at_registration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddDddDbContext<FirstRepoDbContext>(
                o => o.AddRepository<SharedEntity, KeylessOnlyRepository>()));

        Assert.Contains(nameof(KeylessOnlyRepository), error.Message);
        Assert.Contains("IRepository<SharedEntity, Guid>", error.Message);
    }

    // 不传选项即"只登记、不注册仓储"——控制面这类上下文的形态。
    [Fact]
    public void Declaring_without_options_registers_no_repositories()
    {
        var withRepositories = new ServiceCollection();
        withRepositories.AddDddDbContext<FirstRepoDbContext>(o => o.AddDefaultRepositories());
        Assert.Contains(withRepositories, d => d.ServiceType == typeof(Leistd.Ddd.Domain.Repositories.IRepository<SharedEntity>));

        var declaredOnly = new ServiceCollection();
        declaredOnly.AddDddDbContext<FirstRepoDbContext>();
        Assert.DoesNotContain(declaredOnly, d => d.ServiceType == typeof(Leistd.Ddd.Domain.Repositories.IRepository<SharedEntity>));
    }

    /// <summary>默认仓储只给声明了 <c>DbSet</c> 的聚合根；子实体声明 <c>DbSet</c>（为表名走约定）也拿不到仓储。</summary>
    [Fact]
    public void A_child_entity_with_a_dbset_gets_no_default_repository()
    {
        var services = new ServiceCollection();
        services.AddDddDbContext<AggregateDbContext>(o => o.AddDefaultRepositories());

        Assert.Contains(services, d => d.ServiceType == typeof(Leistd.Ddd.Domain.Repositories.IRepository<SharedEntity>));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Leistd.Ddd.Domain.Repositories.IRepository<MappedOnlyEntity>));
    }

    /// <summary>只映射、不暴露 <c>DbSet</c> 的实体可以点名注册仓储。</summary>
    [Fact]
    public void An_entity_without_a_dbset_gets_a_repository_only_when_named_explicitly()
    {
        var defaultsOnly = new ServiceCollection();
        defaultsOnly.AddDddDbContext<MappedOnlyDbContext>(o => o.AddDefaultRepositories());
        Assert.DoesNotContain(defaultsOnly,
            d => d.ServiceType == typeof(Leistd.Ddd.Domain.Repositories.IRepository<MappedOnlyEntity>));

        var named = new ServiceCollection();
        named.AddDddDbContext<MappedOnlyDbContext>(o => o.AddDefaultRepository<MappedOnlyEntity>());
        Assert.Contains(named,
            d => d.ServiceType == typeof(Leistd.Ddd.Domain.Repositories.IRepository<MappedOnlyEntity>));
    }

    private sealed class MappedOnlyEntity : Entity<Guid>
    {
        public string Name { get; set; } = "";
    }

    private sealed class AggregateDbContext(DbContextOptions<AggregateDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<SharedEntity> Items => Set<SharedEntity>();

        public DbSet<MappedOnlyEntity> Lines => Set<MappedOnlyEntity>();
    }

    /// <summary>只在 <c>OnModelCreating</c> 里映射实体，不暴露 <c>DbSet</c>。</summary>
    private sealed class MappedOnlyDbContext(DbContextOptions<MappedOnlyDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<MappedOnlyEntity>().HasKey(x => x.Id);
    }
}
