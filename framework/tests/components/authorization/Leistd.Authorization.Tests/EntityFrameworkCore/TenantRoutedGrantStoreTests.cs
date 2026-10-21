using Leistd.Authorization.Constants;
using Leistd.Authorization.EntityFrameworkCore;
using Leistd.Authorization.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Data.Connections;
using Leistd.MultiTenancy.Context;
using Leistd.Authorization.Tests.TestDoubles;

namespace Leistd.Authorization.Tests.EntityFrameworkCore;

/// <summary>授权存储必须写进<b>工作单元绑定的那个</b>数据库，并进同一个事务。</summary>
/// <remarks>
/// <para>守的是租户路由的失败边界，刻意走完整链路：真实 <c>IUnitOfWorkManager</c> +
/// 真实 <c>IConnectionStringResolver</c> + 宿主形态的 <c>AddDbContext</c> 回调
/// （读 <c>DbContextCreationContext.Current</c>，回落默认连接）。用
/// <c>FixedDbContextProvider</c> 的单元测试<b>覆盖不到这个缺陷</b>——它直接把上下文递进去，
/// 绕过了连接解析这一步，而缺陷恰恰在那一步。</para>
/// <para>缺陷形态：存储直接注入 <c>TDbContext</c> 时，DI 解析走的是宿主回调的<b>回落分支</b>
/// （<c>DbContextCreationContext.Current</c> 为 null），于是独立库租户的授予落到默认连接上，
/// 且脱离工作单元事务。两者都不报错。</para>
/// </remarks>
public sealed class TenantRoutedGrantStoreTests : IAsyncLifetime
{
    private const string RoleId = "r-tenant-scoped";

    private readonly Guid _tenantId = Guid.NewGuid();

    private SqliteConnection _hostAnchor = default!;
    private SqliteConnection _tenantAnchor = default!;
    private string _hostConnectionString = default!;
    private string _tenantConnectionString = default!;
    private ServiceProvider _services = default!;

    public async Task InitializeAsync()
    {
        _hostConnectionString = $"Data Source=host-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _tenantConnectionString = $"Data Source=tenant-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        // 共享内存库需要一个常开连接锚住生命周期
        _hostAnchor = new SqliteConnection(_hostConnectionString);
        _tenantAnchor = new SqliteConnection(_tenantConnectionString);
        await _hostAnchor.OpenAsync();
        await _tenantAnchor.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMultiTenancyCore();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddPermissionAuthorizationEfCore<RoutedDbContext>();
        services.AddSingleton<IPermissionDefinitionProvider, TestPermissionDefinitionProvider>();

        // 租户感知解析器：有租户上下文就给租户库，否则宿主库
        services.AddSingleton<IConnectionStringResolver>(
            new TenantAwareResolver(_hostConnectionString, _tenantConnectionString));

        // 与模板同形：回调读 DbContextCreationContext.Current，回落默认连接。
        // 只有 IDbContextProvider 会设置那个值——这正是本测试要钉住的那一环
        services.AddDbContext<RoutedDbContext>((_, options) =>
            options.UseSqlite(DbContextCreationContext.Current?.ConnectionString ?? _hostConnectionString));

        _services = services.BuildServiceProvider();

        foreach (var connectionString in new[] { _hostConnectionString, _tenantConnectionString })
        {
            var options = new DbContextOptionsBuilder<RoutedDbContext>().UseSqlite(connectionString).Options;
            await using var dbContext = new RoutedDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _hostAnchor.DisposeAsync();
        await _tenantAnchor.DisposeAsync();
    }

    private async Task<int> CountGrantsAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RoutedDbContext>().UseSqlite(connectionString).Options;
        await using var dbContext = new RoutedDbContext(options);
        return await dbContext.Set<PermissionGrantRecord>().CountAsync();
    }

    [Fact]
    public async Task A_grant_written_under_a_tenant_lands_in_the_tenant_database()
    {
        await using var scope = _services.CreateAsyncScope();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        using (currentTenant.Change(_tenantId))
        {
            using var unitOfWork = manager.Begin();

            // 在工作单元内解析：管理器必须拿到工作单元绑定的那个上下文
            await scope.ServiceProvider
                .GetRequiredService<IPermissionGrantManager>()
                .GrantAsync(TestPermissionDefinitionProvider.OrdersRead,
                    PermissionGrantProviderNames.Role, RoleId);

            await unitOfWork.CompleteAsync();
        }

        // 若从容器直接解析上下文：落到宿主库（回调走回落分支），租户库里什么都没有
        Assert.Equal(0, await CountGrantsAsync(_hostConnectionString));
        Assert.True(await CountGrantsAsync(_tenantConnectionString) > 0,
            "grant must land in the tenant database");
    }

    [Fact]
    public async Task A_grant_is_discarded_when_the_unit_of_work_rolls_back()
    {
        await using var scope = _services.CreateAsyncScope();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        using (currentTenant.Change(_tenantId))
        {
            using var unitOfWork = manager.Begin();

            await scope.ServiceProvider
                .GetRequiredService<IPermissionGrantManager>()
                .GrantAsync(TestPermissionDefinitionProvider.OrdersRead,
                    PermissionGrantProviderNames.Role, RoleId);

            // 不 Complete，直接回滚：管理器自己 SaveChanges 过，但那必须发生在
            // 工作单元的事务里——否则回滚拦不住它
            await unitOfWork.RollbackAsync();
        }

        Assert.Equal(0, await CountGrantsAsync(_tenantConnectionString));
        Assert.Equal(0, await CountGrantsAsync(_hostConnectionString));
    }

    [Fact]
    public async Task A_grant_written_without_a_tenant_lands_in_the_host_database()
    {
        await using var scope = _services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        using var unitOfWork = manager.Begin();

        await scope.ServiceProvider
            .GetRequiredService<IPermissionGrantManager>()
            .GrantAsync(TestPermissionDefinitionProvider.OrdersRead,
                PermissionGrantProviderNames.Role, RoleId);

        await unitOfWork.CompleteAsync();

        Assert.True(await CountGrantsAsync(_hostConnectionString) > 0);
        Assert.Equal(0, await CountGrantsAsync(_tenantConnectionString));
    }

    [Fact]
    public async Task A_di_resolving_provider_would_send_the_grant_to_the_wrong_database()
    {
        // 本条钉住上面那些断言的**鉴别力**：同一条代码路径，只把 IDbContextProvider 换成
        // "从 DI 直接解析 TDbContext"，落库目标就从租户库翻到宿主库。
        //
        // 换句话说，如果哪天有人把存储改回直接注入 TDbContext，
        // A_grant_written_under_a_tenant_lands_in_the_tenant_database 一定会红——它不是一条空测试。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMultiTenancyCore();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddPermissionAuthorizationEfCore<RoutedDbContext>();
        services.AddSingleton<IPermissionDefinitionProvider, TestPermissionDefinitionProvider>();
        services.AddSingleton<IConnectionStringResolver>(
            new TenantAwareResolver(_hostConnectionString, _tenantConnectionString));
        services.AddDbContext<RoutedDbContext>((_, options) =>
            options.UseSqlite(DbContextCreationContext.Current?.ConnectionString ?? _hostConnectionString));

        // 覆盖掉真实提供器：不读 DbContextCreationContext，直接问容器要上下文
        services.AddTransient<IDbContextProvider<RoutedDbContext>, DiResolvingDbContextProvider<RoutedDbContext>>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        using (currentTenant.Change(_tenantId))
        {
            using var unitOfWork = manager.Begin();
            await scope.ServiceProvider
                .GetRequiredService<IPermissionGrantManager>()
                .GrantAsync(TestPermissionDefinitionProvider.OrdersRead,
                    PermissionGrantProviderNames.Role, "r-defect-probe");
            await unitOfWork.CompleteAsync();
        }

        // 缺陷形态：租户明明在上下文里，授予却落进了宿主库
        Assert.True(await CountGrantsAsync(_hostConnectionString) > 0,
            "the defect shape must be reproducible, otherwise the tests above prove nothing");
        Assert.Equal(0, await CountGrantsAsync(_tenantConnectionString));
    }


    /// <summary>按当前租户上下文给出连接串，模拟模板的 Identity/Local 解析器。</summary>
    private sealed class TenantAwareResolver(string hostConnectionString, string tenantConnectionString)
        : IConnectionStringResolver
    {
        public Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
        {
            // 直接读静态访问器：ICurrentTenant 只是它的薄壳，测试里不必再走一遍 DI，
            // 也避免解析器与容器之间的构造期循环
            var tenantId = Leistd.MultiTenancy.Context.AsyncLocalCurrentTenantAccessor
                .Instance.Current?.TenantId;

            return Task.FromResult(tenantId is null ? hostConnectionString : tenantConnectionString);
        }
    }

    /// <summary>不读 <c>DbContextCreationContext</c>，直接问容器要上下文——用来证明用例能区分两种路由。</summary>
    private sealed class DiResolvingDbContextProvider<TDbContext>(IServiceProvider serviceProvider)
        : IDbContextProvider<TDbContext>
        where TDbContext : DbContext
    {
        public Task<TDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(serviceProvider.GetRequiredService<TDbContext>());
    }

    private sealed class RoutedDbContext(DbContextOptions<RoutedDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigurePermissionAuthorization();
        }
    }
}
