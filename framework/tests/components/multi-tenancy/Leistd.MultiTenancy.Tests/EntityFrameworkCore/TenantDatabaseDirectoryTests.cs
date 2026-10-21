using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.Stores;
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

/// <summary>本地库目录：按指纹合并、按启用过滤、逐租户隔离，且<b>不下发连接串</b>。</summary>
/// <remarks>
/// 逐库作业只需要"有哪些库、用哪个租户进得去"。把连接串一起下发会逼着常驻服务申请迁移权限，
/// 那等于让它能拉取全部租户的明文连接串——这条路必须与迁移分开。
/// </remarks>
public class TenantDatabaseDirectoryTests : IAsyncLifetime
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
        // 目录由 AddMultiTenancyEfCore 自己注册：它是控制库的存储。夹具不额外补注册，
        // 这样"注册跑到别的入口去了"会在这里直接解析失败
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
    private ITenantConnectionConfigurationManager Connections =>
        _provider.GetRequiredService<ITenantConnectionConfigurationManager>();
    private ITenantDatabaseDirectory Directory => _provider.GetRequiredService<ITenantDatabaseDirectory>();

    /// <summary>只调 <c>AddMultiTenancyEfCore</c> 就能解析出库目录，不必再调本地连接解析。</summary>
    /// <remarks>
    /// 目录读的是控制库，注册跟控制库的其余存储走，不挂在连接解析入口上：只承担控制面、
    /// 自己不分库的服务不会调那个入口，而它同样要对外提供库清单。
    /// </remarks>
    [Fact]
    public void The_directory_comes_with_the_control_database_stores()
        => Assert.IsType<EfCoreTenantDatabaseDirectory<TestDbContext>>(Directory);

    /// <summary>共用一个库的租户合并成一条，住户按标识升序带出。</summary>
    [Fact]
    public async Task Tenants_sharing_a_database_are_merged_and_carry_their_members()
    {
        // 登记连接要求租户处于停用态（改路由不能在服务中途做），建完再启用
        var first = await CreateWithConnectionAsync("first", "Host=shared;Password=s3cret");
        var second = await CreateWithConnectionAsync("second", "Host=shared;Password=s3cret");
        var alone = await CreateWithConnectionAsync("alone", "Host=alone");

        var listed = await Directory.GetDatabasesAsync("default", activeOnly: true);

        Assert.Equal(2, listed.Databases.Count);
        var shared = listed.Databases.Single(entry => entry.TenantIds.Count == 2);
        Assert.Equal([first, second], shared.TenantIds.Order());
        // 连接串不出现在结果里，指纹不可逆推
        Assert.DoesNotContain("s3cret", string.Join('|', listed.Databases.Select(entry => entry.Fingerprint)));
    }

    /// <summary>停用租户的库算不算由调用方决定。</summary>
    /// <remarks>
    /// 保留期作业要连停用租户的数据一起处理（合规义务不随停用消失），
    /// 而刷新进程内状态一类作业不该去连可能已下线的库——框架不替调用方选。
    /// </remarks>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task The_active_filter_decides_whether_deactivated_tenants_count(bool activeOnly, int expected)
    {
        await CreateWithConnectionAsync("running", "Host=running");
        await CreateWithConnectionAsync("stopped", "Host=stopped", activate: false);

        var listed = await Directory.GetDatabasesAsync("default", activeOnly);

        Assert.Equal(expected, listed.Databases.Count);
    }

    /// <summary>没登记连接的租户住在宿主库里：不算独立库、也不算失败。</summary>
    /// <remarks>
    /// 目录刻意不回这些租户的清单——它的大小等于共享库的租户数，可能上万且要跨 HTTP 边界，
    /// 而逐库作业进宿主库用的是宿主配置、不需要某个租户。要查"某个租户在哪个库"查连接登记表。
    /// </remarks>
    [Fact]
    public async Task A_tenant_without_registrations_is_not_a_dedicated_database()
    {
        await Tenants.CreateAsync("plain", null, isActive: true);

        var listed = await Directory.GetDatabasesAsync("default", activeOnly: true);

        Assert.Empty(listed.Databases);
        Assert.Empty(listed.FailedTenants);
    }

    /// <summary>精确名优先于默认名，与解析链同一口径。</summary>
    [Fact]
    public async Task An_exact_name_wins_over_the_default_one()
    {
        var tenant = await Tenants.CreateAsync("acme", null, isActive: false);
        await Connections.SetAsync(tenant.Id, "default", "Host=fallback", expectedVersion: null);
        await Connections.SetAsync(tenant.Id, "crm", "Host=crm", expectedVersion: null);
        await Tenants.SetActiveAsync(tenant.Id, true);

        var listed = await Directory.GetDatabasesAsync("crm", activeOnly: true);

        Assert.Equal(TenantDatabaseFingerprint.Of("Host=crm"), Assert.Single(listed.Databases).Fingerprint);
    }

    /// <summary>登记过连接、却解析不出目标名：进失败清单，不能当成"住在宿主库里"。</summary>
    /// <remarks>
    /// 两者在目录里都表现为"没有独立库条目"，但含义相反：前者有自己的库、这一轮进不去，
    /// 静默跳过就等于让作业漏掉一个库；后者本来就该用宿主配置。运行时解析链遇到同一情形
    /// 是失败关闭，目录必须跟它同口径。
    /// </remarks>
    [Fact]
    public async Task A_registered_tenant_that_cannot_resolve_the_name_fails_instead_of_falling_back_to_the_host()
    {
        var tenant = await Tenants.CreateAsync("acme", null, isActive: false);
        await Connections.SetAsync(tenant.Id, "crm", "Host=crm", expectedVersion: null);
        await Tenants.SetActiveAsync(tenant.Id, true);

        var listed = await Directory.GetDatabasesAsync("default", activeOnly: true);

        Assert.Empty(listed.Databases);
        Assert.Equal(tenant.Id, Assert.Single(listed.FailedTenants).TenantId);
    }

    private async Task<Guid> CreateWithConnectionAsync(string name, string connectionString, bool activate = true)
    {
        var tenant = await Tenants.CreateAsync(name, null, isActive: false);
        await Connections.SetAsync(tenant.Id, "default", connectionString, expectedVersion: null);
        if (activate)
        {
            await Tenants.SetActiveAsync(tenant.Id, true);
        }

        return tenant.Id;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureMultiTenancy();
        }
    }
}
