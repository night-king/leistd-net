using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.Stores;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

public class TenantConnectionConfigurationStoreTests : IAsyncLifetime
{
    private SqliteConnection _connection = default!;
    private ServiceProvider _provider = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMultiTenancyEfCore<TestDbContext>();
        _provider = services.BuildServiceProvider();

        await _provider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private ITenantManager Tenants => _provider.GetRequiredService<ITenantManager>();
    private ITenantConnectionConfigurationManager Manager =>
        _provider.GetRequiredService<ITenantConnectionConfigurationManager>();
    private ITenantConnectionConfigurationStore Store =>
        _provider.GetRequiredService<ITenantConnectionConfigurationStore>();

    // 租户不存在与"存在但没登记"是两种结果：前者 null 让调用方失败关闭，后者回落到服务自己的配置
    [Fact]
    public async Task An_unknown_tenant_returns_null()
    {
        Assert.Null(await Store.FindAsync(Guid.NewGuid(), "crm"));
    }

    [Fact]
    public async Task A_tenant_without_registrations_reports_no_connections()
    {
        var tenant = await Tenants.CreateAsync("plain", null, isActive: false);

        var lookup = await Store.FindAsync(tenant.Id, "crm");

        Assert.NotNull(lookup);
        Assert.Equal(tenant.Id, lookup.TenantId);
        Assert.False(lookup.HasAnyConnection);
        Assert.Null(lookup.Connection);
    }

    [Fact]
    public async Task An_exact_name_hit_is_returned()
    {
        var tenant = await Tenants.CreateAsync("exact", null, isActive: false);
        await Manager.SetAsync(tenant.Id, "crm", "Host=crm", expectedVersion: null);

        var lookup = await Store.FindAsync(tenant.Id, "Crm");

        Assert.True(lookup!.HasAnyConnection);
        Assert.Equal("crm", lookup.Connection!.Name);
        Assert.Equal("Host=crm", lookup.Connection.ConnectionString);
    }

    [Fact]
    public async Task A_default_named_registration_is_the_fallback()
    {
        var tenant = await Tenants.CreateAsync("fallback", null, isActive: false);
        await Manager.SetAsync(tenant.Id, "default", "Host=one-db", expectedVersion: null);

        var lookup = await Store.FindAsync(tenant.Id, "crm");

        Assert.Equal("default", lookup!.Connection!.Name);
        Assert.Equal("Host=one-db", lookup.Connection.ConnectionString);
    }

    [Fact]
    public async Task An_exact_name_wins_over_the_default_name()
    {
        var tenant = await Tenants.CreateAsync("both", null, isActive: false);
        await Manager.SetAsync(tenant.Id, "default", "Host=one-db", expectedVersion: null);
        await Manager.SetAsync(tenant.Id, "crm", "Host=crm-db", expectedVersion: null);

        Assert.Equal("Host=crm-db", (await Store.FindAsync(tenant.Id, "crm"))!.Connection!.ConnectionString);
        Assert.Equal("Host=one-db", (await Store.FindAsync(tenant.Id, "foundation"))!.Connection!.ConnectionString);
    }

    // 分库租户缺这个名字：由调用方失败关闭，存储只如实报告
    [Fact]
    public async Task A_registered_tenant_missing_this_name_reports_no_connection()
    {
        var tenant = await Tenants.CreateAsync("partial", null, isActive: false);
        await Manager.SetAsync(tenant.Id, "foundation", "Host=foundation", expectedVersion: null);

        var lookup = await Store.FindAsync(tenant.Id, "crm");

        Assert.True(lookup!.HasAnyConnection);
        Assert.Null(lookup.Connection);
    }

    [Fact]
    public async Task Reads_the_control_database_independently_of_current_tenant()
    {
        var target = await Tenants.CreateAsync("target", null, isActive: false);
        var other = await Tenants.CreateAsync("other", null, isActive: false);
        await Manager.SetAsync(target.Id, "default", "Host=target", expectedVersion: null);
        var currentTenant = _provider.GetRequiredService<ICurrentTenant>();

        using (currentTenant.Change(other.Id, other.Name))
        {
            var lookup = await Store.FindAsync(target.Id, "default");
            Assert.Equal(target.Id, lookup!.TenantId);
        }
    }

    [Fact]
    public void Tenant_configuration_does_not_gain_connection_fields()
    {
        var propertyNames = typeof(TenantConfiguration).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Connection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_migration_list_resolves_every_registered_tenant_by_name()
    {
        var exact = await Tenants.CreateAsync("exact", null, isActive: false);
        var fallback = await Tenants.CreateAsync("fallback", null, isActive: false);
        await Tenants.CreateAsync("plain", null, isActive: false);
        await Manager.SetAsync(exact.Id, "crm", "Host=crm-db", expectedVersion: null);
        await Manager.SetAsync(fallback.Id, "default", "Host=one-db", expectedVersion: null);

        var list = await Store.GetListAsync("Crm");
        var connections = list.Connections;

        // 没登记的租户不出现——它跟着宿主自己的库迁移
        Assert.Equal(2, connections.Count);
        Assert.Equal("Host=crm-db", connections.Single(x => x.TenantId == exact.Id).ConnectionString);
        Assert.Equal("default", connections.Single(x => x.TenantId == fallback.Id).Name);
        Assert.Empty(list.FailedTenants);
    }

    /// <summary>一个租户解析不出这个名字，单列为失败，其余租户照常返回。</summary>
    /// <remarks>
    /// 不能整体抛出：那样一个租户的配置错误会挡住所有租户的迁移。也不能静默略过它——
    /// 那个库会停在旧结构上，下一次发版才炸——所以它必须出现在失败清单里。
    /// </remarks>
    [Fact]
    public async Task The_migration_list_reports_an_unresolvable_tenant_without_blocking_the_others()
    {
        var healthy = await Tenants.CreateAsync("healthy", null, isActive: false);
        var partial = await Tenants.CreateAsync("partial", null, isActive: false);
        await Manager.SetAsync(healthy.Id, "crm", "Host=crm-db", expectedVersion: null);
        await Manager.SetAsync(partial.Id, "foundation", "Host=foundation;Password=secret", expectedVersion: null);

        var list = await Store.GetListAsync("crm");

        Assert.Equal(healthy.Id, Assert.Single(list.Connections).TenantId);
        var failure = Assert.Single(list.FailedTenants);
        Assert.Equal(partial.Id, failure.TenantId);
        Assert.DoesNotContain("secret", failure.Reason);
    }

    /// <summary>已删除租户的连接不可达。</summary>
    /// <remarks>
    /// 这条锁住的缺陷是<b>非对称的删除边界</b>：本地解析器自己 join 了「租户未删」，
    /// 而 Resource 宿主拿路由走的是本存储经 HTTP 暴露的读路径。本存储少这道 join 时，
    /// 删掉一个租户之后 Identity 拒绝它、Resource 却继续按它的连接串路由到它的库。
    /// 控制面上下文是普通 DbContext，没有软删除过滤器兜底，判据只能显式写。
    /// </remarks>
    [Fact]
    public async Task A_deleted_tenant_has_no_reachable_connection()
    {
        var tenant = await Tenants.CreateAsync("doomed", null, isActive: false);
        await Manager.SetAsync(tenant.Id, "default", "Host=doomed;Password=doomed-secret", expectedVersion: null);
        Assert.NotNull(await Store.FindAsync(tenant.Id, "default"));

        await Tenants.DeleteAsync(tenant.Id);

        // 行仍在库里（软删只翻 TenantRecord 的标志），但不再可达
        Assert.Null(await Store.FindAsync(tenant.Id, "default"));
        var list = await Store.GetListAsync("default");
        Assert.DoesNotContain(list.Connections, item => item.TenantId == tenant.Id);
        Assert.DoesNotContain(list.FailedTenants, item => item.TenantId == tenant.Id);
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureMultiTenancy();
        }
    }
}
