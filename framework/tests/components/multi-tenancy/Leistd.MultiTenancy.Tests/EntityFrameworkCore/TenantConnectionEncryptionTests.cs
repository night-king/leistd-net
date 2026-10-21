using System.Collections.Concurrent;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.EntityFrameworkCore.Stores;
using Leistd.MultiTenancy.Stores;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

/// <summary>连接串加密存储：库里只有密文、读取时正确解密、换了密钥环就拒绝，日志与异常里不出现明文。</summary>
/// <remarks>
/// 上下文刻意开启 EF 敏感数据日志：即使运维为排障打开它，参数里也只能看到密文。
/// </remarks>
public sealed class TenantConnectionEncryptionTests : IAsyncLifetime
{
    private const string Secret = "Pa55word-do-not-leak";
    private const string ConnectionString = $"Host=tenant-db;Database=acme;Username=acme;Password={Secret}";
    private const string Name = "crm";

    private readonly ConcurrentQueue<string> _efLog = new();
    private SqliteConnection _connection = default!;
    private ServiceProvider _provider = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(options => options
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .LogTo(_efLog.Enqueue, LogLevel.Debug));
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

    private ITenantConnectionConfigurationManager Manager =>
        _provider.GetRequiredService<ITenantConnectionConfigurationManager>();

    private ITenantConnectionConfigurationStore Store =>
        _provider.GetRequiredService<ITenantConnectionConfigurationStore>();

    private async Task<Guid> RegisterTenantAsync()
    {
        var tenant = await _provider.GetRequiredService<ITenantManager>()
            .CreateAsync($"tenant-{Guid.NewGuid():N}", null, isActive: false);
        await Manager.SetAsync(tenant.Id, Name, ConnectionString, expectedVersion: null);
        return tenant.Id;
    }

    private async Task<string?> ReadStoredValueAsync(Guid tenantId)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT \"{nameof(TenantConnectionRecord.ProtectedConnectionString)}\" " +
                              $"FROM \"{nameof(TenantConnectionRecord)}\" WHERE \"TenantId\" = $id";
        command.Parameters.AddWithValue("$id", tenantId.ToString().ToUpperInvariant());
        var value = await command.ExecuteScalarAsync();
        if (value is null)
        {
            // SQLite 的 Guid 文本大小写取决于提供程序；两种都试
            command.Parameters["$id"].Value = tenantId.ToString();
            value = await command.ExecuteScalarAsync();
        }

        return value as string;
    }

    // 数据库、备份与只读账号能看到的只有这一列：它不能是明文，也不能含明文片段
    [Fact]
    public async Task Only_ciphertext_is_stored()
    {
        var tenantId = await RegisterTenantAsync();

        var stored = await ReadStoredValueAsync(tenantId);

        Assert.False(string.IsNullOrEmpty(stored));
        Assert.DoesNotContain(Secret, stored);
        Assert.DoesNotContain("Host=", stored);
        Assert.NotEqual(ConnectionString, stored);
    }

    [Fact]
    public async Task Reading_decrypts_the_connection_string()
    {
        var tenantId = await RegisterTenantAsync();

        Assert.Equal(ConnectionString, (await Store.FindAsync(tenantId, Name))!.Connection!.ConnectionString);
        Assert.Equal(ConnectionString, Assert.Single((await Store.GetListAsync(Name)).Connections).ConnectionString);
    }

    [Fact]
    public async Task Updating_re_encrypts_the_new_value()
    {
        var tenantId = await RegisterTenantAsync();
        var before = await ReadStoredValueAsync(tenantId);

        await Manager.SetAsync(tenantId, Name, "Host=moved;Password=moved-secret", expectedVersion: 1);

        var after = await ReadStoredValueAsync(tenantId);
        Assert.NotEqual(before, after);
        Assert.DoesNotContain("moved-secret", after);
        Assert.Equal(
            "Host=moved;Password=moved-secret",
            (await Store.FindAsync(tenantId, Name))!.Connection!.ConnectionString);
    }

    // 改回不分库就是删掉这一行：密文随行消失，解析回落到服务自己的配置
    [Fact]
    public async Task Removing_the_registration_deletes_the_ciphertext()
    {
        var tenantId = await RegisterTenantAsync();

        await Manager.RemoveAsync(tenantId, Name, expectedVersion: 1);

        Assert.Null(await ReadStoredValueAsync(tenantId));
        var lookup = await Store.FindAsync(tenantId, Name);
        Assert.False(lookup!.HasAnyConnection);
        Assert.Null(lookup.Connection);
    }

    // 密钥丢失或进程之间未共享密钥环：拒绝，而不是回落到服务自己的库；异常链里不带明文
    [Fact]
    public async Task A_value_written_with_another_key_ring_is_refused()
    {
        var tenantId = await RegisterTenantAsync();
        var otherKeyRing = new EfCoreTenantConnectionConfigurationStore<TestDbContext>(
            _provider.GetRequiredService<IDbContextProvider<TestDbContext>>(),
            new EphemeralDataProtectionProvider());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => otherKeyRing.FindAsync(tenantId, Name));

        Assert.Contains(tenantId.ToString(), error.Message);
        Assert.Contains(Name, error.Message);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task A_tampered_ciphertext_is_refused()
    {
        var tenantId = await RegisterTenantAsync();
        var db = _provider.GetRequiredService<TestDbContext>();
        var record = await db.Set<TenantConnectionRecord>().SingleAsync(x => x.TenantId == tenantId);
        record.ProtectedConnectionString = "not-a-valid-payload";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.FindAsync(tenantId, Name));
    }

    // 迁移清单里解不开的密文只算这个租户失败：不挡住其他租户，失败原因里也没有明文
    [Fact]
    public async Task A_tampered_ciphertext_fails_only_its_own_tenant_in_the_migration_list()
    {
        var healthy = await RegisterTenantAsync();
        var tampered = await RegisterTenantAsync();
        var db = _provider.GetRequiredService<TestDbContext>();
        var record = await db.Set<TenantConnectionRecord>().SingleAsync(x => x.TenantId == tampered);
        record.ProtectedConnectionString = "not-a-valid-payload";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var list = await Store.GetListAsync(Name);

        Assert.Equal(healthy, Assert.Single(list.Connections).TenantId);
        var failure = Assert.Single(list.FailedTenants);
        Assert.Equal(tampered, failure.TenantId);
        Assert.DoesNotContain(Secret, failure.Reason);
    }

    // 开着 EF 敏感数据日志，写入与读取的日志里也只能出现密文
    [Fact]
    public async Task Ef_logs_never_contain_the_plaintext()
    {
        var tenantId = await RegisterTenantAsync();
        await Store.FindAsync(tenantId, Name);

        Assert.NotEmpty(_efLog);
        Assert.DoesNotContain(_efLog, line => line.Contains(Secret, StringComparison.Ordinal));
    }

    // 校验失败的异常消息只描述规则，不回显调用方传入的连接串
    [Fact]
    public async Task Validation_errors_do_not_echo_the_connection_string()
    {
        var tenant = await _provider.GetRequiredService<ITenantManager>()
            .CreateAsync($"tenant-{Guid.NewGuid():N}", null, isActive: false);
        var overlong = ConnectionString + new string('x', TenantConnectionConfiguration.MaxConnectionStringLength);

        var tooLong = await Assert.ThrowsAsync<BusinessException>(
            () => Manager.SetAsync(tenant.Id, Name, overlong, expectedVersion: null));

        Assert.DoesNotContain(Secret, tooLong.ToString());
    }

    // 快照、实体与迁移目标的文本形态都不带明文：它们最容易被随手写进日志
    [Fact]
    public async Task String_representations_do_not_expose_the_connection_string()
    {
        var tenantId = await RegisterTenantAsync();
        var lookup = (await Store.FindAsync(tenantId, Name))!;
        var record = await _provider.GetRequiredService<TestDbContext>()
            .Set<TenantConnectionRecord>().AsNoTracking().SingleAsync(x => x.TenantId == tenantId);

        Assert.DoesNotContain(Secret, lookup.ToString());
        Assert.DoesNotContain(Secret, lookup.Connection!.ToString());
        Assert.DoesNotContain(record.ProtectedConnectionString, record.ToString());
        Assert.DoesNotContain(Secret, new TenantMigrationTarget(tenantId, ConnectionString).ToString());
        Assert.DoesNotContain(Secret, new TenantMigrationConnection(tenantId, Name, ConnectionString).ToString());
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureMultiTenancy();
    }
}
