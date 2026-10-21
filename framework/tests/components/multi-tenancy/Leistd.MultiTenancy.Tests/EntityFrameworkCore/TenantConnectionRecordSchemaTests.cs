using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.EntityConfigurations;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

/// <summary>租户连接登记的表级形态：主键是 <c>(TenantId, Name)</c>，密文非空，同一租户可登记多条。</summary>
/// <remarks>以行是否存在判断登记状态；复合主键防止同名重复，密文非空防止出现既不能回落也无连接可用的空行。</remarks>
public class TenantConnectionRecordSchemaTests : IAsyncLifetime
{
    private SqliteConnection _connection = default!;
    private SchemaDbContext _dbContext = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<SchemaDbContext>().UseSqlite(_connection).Options;
        _dbContext = new SchemaDbContext(options);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task One_tenant_can_register_several_named_connections()
    {
        var tenantId = await SeedTenantAsync();

        Add(tenantId, "default", "ciphertext-default");
        Add(tenantId, "crm", "ciphertext-crm");
        Add(tenantId, "foundation", "ciphertext-foundation");
        await _dbContext.SaveChangesAsync();

        Assert.Equal(3, await _dbContext.Set<TenantConnectionRecord>().CountAsync(x => x.TenantId == tenantId));
    }

    // 同名重复登记会让"这个名字用哪一条"变成不确定，必须由主键拦下
    [Fact]
    public async Task The_same_name_cannot_be_registered_twice_for_one_tenant()
    {
        var tenantId = await SeedTenantAsync();
        Add(tenantId, "crm", "ciphertext-one");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        Add(tenantId, "crm", "ciphertext-two");

        Assert.NotNull(await Record.ExceptionAsync(() => _dbContext.SaveChangesAsync()));
        _dbContext.ChangeTracker.Clear();
    }

    // 不同租户用同一个名字是正常的：crm 服务在每个分库租户那里都有一条
    [Fact]
    public async Task Different_tenants_may_use_the_same_name()
    {
        var first = await SeedTenantAsync();
        var second = await SeedTenantAsync();

        Add(first, "crm", "ciphertext-one");
        Add(second, "crm", "ciphertext-two");
        await _dbContext.SaveChangesAsync();

        Assert.Equal(2, await _dbContext.Set<TenantConnectionRecord>().CountAsync(x => x.Name == "crm"));
    }

    // 空密文的行解析既不回落也取不到值，是纯粹的坏数据
    [Fact]
    public async Task A_registration_without_a_ciphertext_is_rejected_by_the_database()
    {
        var tenantId = await SeedTenantAsync();
        Add(tenantId, "crm", null!);

        Assert.NotNull(await Record.ExceptionAsync(() => _dbContext.SaveChangesAsync()));
        _dbContext.ChangeTracker.Clear();
    }

    [Fact]
    public void The_model_has_a_composite_key_and_no_mode_column()
    {
        var entity = _dbContext.Model.FindEntityType(typeof(TenantConnectionRecord))!;

        Assert.Equal(
            [nameof(TenantConnectionRecord.TenantId), nameof(TenantConnectionRecord.Name)],
            entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(
            TenantConnectionConfiguration.MaxNameLength,
            entity.FindProperty(nameof(TenantConnectionRecord.Name))!.GetMaxLength());
        Assert.Equal(
            TenantConnectionRecordConfiguration.MaxProtectedConnectionStringLength,
            entity.FindProperty(nameof(TenantConnectionRecord.ProtectedConnectionString))!.GetMaxLength());
        Assert.False(entity.FindProperty(nameof(TenantConnectionRecord.ProtectedConnectionString))!.IsNullable);
        // 模式标志位已经没有了：行的存在本身就是判据，随之删掉的还有旧的"模式与连接串一致性"检查约束
        Assert.DoesNotContain(entity.GetProperties(), p => p.Name.Contains("Mode", StringComparison.Ordinal));
    }

    private void Add(Guid tenantId, string name, string protectedConnectionString) =>
        _dbContext.Set<TenantConnectionRecord>().Add(new TenantConnectionRecord
        {
            TenantId = tenantId,
            Name = name,
            ProtectedConnectionString = protectedConnectionString,
            Version = 1
        });

    private async Task<Guid> SeedTenantAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new TenantRecord { Name = $"t{suffix}", NormalizedName = $"T{suffix}".ToUpperInvariant() };
        _dbContext.Set<TenantRecord>().Add(tenant);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return tenant.Id;
    }

    private sealed class SchemaDbContext(DbContextOptions<SchemaDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureMultiTenancy();
    }
}
