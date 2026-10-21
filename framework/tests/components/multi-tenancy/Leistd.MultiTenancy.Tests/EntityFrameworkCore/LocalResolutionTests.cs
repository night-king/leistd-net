using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.Exceptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

/// <summary>
/// 本地解析的三级落点：宿主直连控制库按连接名查登记并解密。
/// 控制库上下文没有软删除过滤器，已删除租户必须由查询入口排除。
/// </summary>
public sealed class LocalResolutionTests : IDisposable
{
    private const string DefaultConnection = "Data Source=shared";
    private const string CrmHostConnection = "Data Source=crm-host";
    private const string ControlConnection = "Data Source=control";
    private const string AcmeConnection = "Data Source=acme;Password=acme-secret";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ControlDbContext _db;
    private readonly SettableCurrentTenant _tenant = new();
    private readonly IDataProtectionProvider _keyRing = new EphemeralDataProtectionProvider();

    public LocalResolutionTests()
    {
        _connection.Open();
        _db = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private LocalConnectionStringResolver<ControlDbContext> Resolver(
        string? controlConnection = ControlConnection,
        string? crmConnection = null,
        IDataProtectionProvider? keyRing = null) => new(
        _tenant,
        _db,
        keyRing ?? _keyRing,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = DefaultConnection,
            ["ConnectionStrings:Crm"] = crmConnection,
            ["ConnectionStrings:Control"] = controlConnection
        }).Build(),
        Microsoft.Extensions.Options.Options.Create(
            new LocalTenantConnectionOptions { ControlPlaneConnectionStringName = "Control" }));

    private async Task<Guid> SeedTenantAsync(bool deleted = false)
    {
        var tenant = new TenantRecord
        {
            Name = $"t-{Guid.NewGuid():N}",
            NormalizedName = $"T-{Guid.NewGuid():N}",
            IsDeleted = deleted
        };
        _db.Add(tenant);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return tenant.Id;
    }

    private async Task RegisterAsync(Guid tenantId, string name, string connectionString)
    {
        _db.Add(new TenantConnectionRecord
        {
            TenantId = tenantId,
            Name = name,
            // 按管理器的方式加密落库
            ProtectedConnectionString = _keyRing
                .CreateProtector(TenantConnectionStringProtector.Purpose)
                .Protect(connectionString),
            Version = 1
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // 控制库固定在宿主连接上，即使处在租户上下文里也不参与路由
    [Fact]
    public async Task The_control_plane_name_always_resolves_to_the_host_connection()
    {
        _tenant.Id = Guid.NewGuid();

        Assert.Equal(ControlConnection, await Resolver().ResolveAsync("Control"));
        Assert.Equal(DefaultConnection, await Resolver(controlConnection: null).ResolveAsync("Control"));
    }

    [Fact]
    public async Task Without_a_tenant_the_host_connection_is_used()
    {
        Assert.Equal(DefaultConnection, await Resolver().ResolveAsync("Default"));
        Assert.Equal(CrmHostConnection, await Resolver(crmConnection: CrmHostConnection).ResolveAsync("Crm"));
    }

    // 一条连接都没登记 = 不分库
    [Fact]
    public async Task A_tenant_without_any_registered_connection_uses_the_host_connection()
    {
        _tenant.Id = await SeedTenantAsync();

        Assert.Equal(DefaultConnection, await Resolver().ResolveAsync("Default"));
        Assert.Equal(CrmHostConnection, await Resolver(crmConnection: CrmHostConnection).ResolveAsync("Crm"));
    }

    [Fact]
    public async Task An_exact_name_hit_is_used_and_decrypted()
    {
        _tenant.Id = await SeedTenantAsync();
        await RegisterAsync(_tenant.Id!.Value, "crm", AcmeConnection);

        Assert.Equal(AcmeConnection, await Resolver().ResolveAsync("Crm"));
    }

    // 一租户一库、各服务不同 schema：只登记 default 一条
    [Fact]
    public async Task A_default_named_registration_is_the_fallback_for_any_name()
    {
        _tenant.Id = await SeedTenantAsync();
        await RegisterAsync(_tenant.Id!.Value, "default", AcmeConnection);

        Assert.Equal(AcmeConnection, await Resolver().ResolveAsync("Crm"));
        Assert.Equal(AcmeConnection, await Resolver().ResolveAsync("Foundation"));
    }

    // 精确名优先于默认名
    [Fact]
    public async Task An_exact_name_wins_over_the_default_name()
    {
        _tenant.Id = await SeedTenantAsync();
        await RegisterAsync(_tenant.Id!.Value, "default", "Data Source=acme-shared");
        await RegisterAsync(_tenant.Id!.Value, "crm", AcmeConnection);

        Assert.Equal(AcmeConnection, await Resolver().ResolveAsync("Crm"));
        Assert.Equal("Data Source=acme-shared", await Resolver().ResolveAsync("Foundation"));
    }

    // 唯一的失败关闭点：分库租户缺这个服务的连接，也没有默认名可回落
    [Fact]
    public async Task A_registered_tenant_missing_this_name_is_refused()
    {
        _tenant.Id = await SeedTenantAsync();
        await RegisterAsync(_tenant.Id!.Value, "foundation", AcmeConnection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Resolver(crmConnection: CrmHostConnection).ResolveAsync("Crm"));

        Assert.DoesNotContain("acme-secret", error.ToString());
    }

    // 管理员填 Crm 还是 crm 命中同一行
    [Theory]
    [InlineData("Crm")]
    [InlineData("crm")]
    [InlineData("CRM")]
    public async Task Names_are_matched_case_insensitively(string requested)
    {
        _tenant.Id = await SeedTenantAsync();
        await RegisterAsync(_tenant.Id!.Value, "crm", AcmeConnection);

        Assert.Equal(AcmeConnection, await Resolver().ResolveAsync(requested));
    }

    // 密钥环不同（未共享、密钥丢失）时拒绝；拒绝消息不带明文
    [Fact]
    public async Task A_connection_string_that_cannot_be_decrypted_is_refused()
    {
        _tenant.Id = await SeedTenantAsync();
        await RegisterAsync(_tenant.Id!.Value, "default", AcmeConnection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Resolver(keyRing: new EphemeralDataProtectionProvider()).ResolveAsync("Default"));

        Assert.DoesNotContain("acme-secret", error.ToString());
    }

    // 软删租户的连接行仍在库里；漏掉这道排除就是一条越过删除边界的读路径
    [Fact]
    public async Task A_deleted_tenant_has_no_route()
    {
        _tenant.Id = await SeedTenantAsync(deleted: true);
        await RegisterAsync(_tenant.Id!.Value, "default", AcmeConnection);

        await Assert.ThrowsAsync<TenantNotFoundException>(() => Resolver().ResolveAsync("Default"));
    }

    [Fact]
    public async Task An_unknown_tenant_has_no_route()
    {
        _tenant.Id = Guid.NewGuid();

        await Assert.ThrowsAsync<TenantNotFoundException>(() => Resolver().ResolveAsync("Default"));
    }

    [Fact]
    public async Task An_invalid_connection_name_is_rejected()
    {
        _tenant.Id = await SeedTenantAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Resolver().ResolveAsync("crm_db"));
    }

    public sealed class ControlDbContext(DbContextOptions<ControlDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureMultiTenancy();
    }
}
