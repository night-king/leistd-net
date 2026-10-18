using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Leistd.MultiTenancy.Context;
using Leistd.Settings.Definitions;
using Leistd.Settings.EntityFrameworkCore;
using Leistd.Settings.EntityFrameworkCore.Entities;
using Leistd.Settings.EntityFrameworkCore.Stores;
using Leistd.Settings.Exceptions;
using Leistd.TestBase.Doubles;
using Xunit;

namespace Leistd.Settings.Tests.EntityFrameworkCore;

/// <summary>设置存储与表结构。</summary>
/// <remarks>
/// 用 SQLite 而不是 InMemory：唯一索引与长度约束只有真正建库时才产生 DDL，
/// InMemory 全内存求值会让整个 <c>SettingRecordConfiguration</c> 静默通过。
/// </remarks>
public sealed class EfCoreSettingStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SettingDbContext _db;
    private readonly EfCoreSettingStore<SettingDbContext> _store;

    public EfCoreSettingStoreTests()
    {
        _connection.Open();
        _db = new SettingDbContext(new DbContextOptionsBuilder<SettingDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        // 宿主上下文（TenantId 为 null）是最容易被 NULL 语义放过的层级，默认就用它建店。
        _store = NewStore(tenantId: null);
    }

    private EfCoreSettingStore<SettingDbContext> NewStore(Guid? tenantId) => new(
        new FixedDbContextProvider<SettingDbContext>(_db),
        new FakeCurrentTenant(tenantId));

    public sealed class SettingDbContext(DbContextOptions<SettingDbContext> options) : DbContext(options)
    {
        public DbSet<SettingRecord> Settings => Set<SettingRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureSettings();
    }

    // 表名跟着宿主的 DbSet 属性名走，组件不写死它。
    [Fact]
    public void The_table_name_follows_the_host_DbSet_name()
    {
        Assert.Equal("Settings", _db.Model.FindEntityType(typeof(SettingRecord))!.GetTableName());
    }

    [Fact]
    public async Task Values_written_at_a_level_are_read_back_from_that_level()
    {
        await _store.SetAsync("Display.Language", "en-US", SettingScopes.Tenant, null);
        await _store.SetAsync("Display.Language", "ja-JP", SettingScopes.User, "user-1");

        var tenant = await _store.GetAllAsync(SettingScopes.Tenant, null);
        var user = await _store.GetAllAsync(SettingScopes.User, "user-1");

        Assert.Equal("en-US", tenant["Display.Language"]);
        Assert.Equal("ja-JP", user["Display.Language"]);
    }

    // 用户级的值不能出现在租户级的读取里，否则回落顺序会被越过。
    [Fact]
    public async Task A_user_value_does_not_leak_into_the_tenant_level()
    {
        await _store.SetAsync("Display.Language", "ja-JP", SettingScopes.User, "user-1");

        Assert.Empty(await _store.GetAllAsync(SettingScopes.Tenant, null));
    }

    [Fact]
    public async Task Two_users_do_not_see_each_other_values()
    {
        await _store.SetAsync("Display.Language", "ja-JP", SettingScopes.User, "user-1");
        await _store.SetAsync("Display.Language", "ko-KR", SettingScopes.User, "user-2");

        Assert.Equal("ja-JP", (await _store.GetAllAsync(SettingScopes.User, "user-1"))["Display.Language"]);
        Assert.Equal("ko-KR", (await _store.GetAllAsync(SettingScopes.User, "user-2"))["Display.Language"]);
    }

    // 重复写入同一层级同一名称必须更新既有行，而不是插出第二行让唯一索引报错。
    [Fact]
    public async Task Writing_the_same_setting_twice_updates_in_place()
    {
        await _store.SetAsync("Display.Language", "en-US", SettingScopes.Tenant, null);
        await _store.SetAsync("Display.Language", "ja-JP", SettingScopes.Tenant, null);

        Assert.Equal("ja-JP", (await _store.GetAllAsync(SettingScopes.Tenant, null))["Display.Language"]);
        Assert.Equal(1, await _db.Settings.CountAsync());
    }

    // null 清除该层级的值，读取因此回落到下一层。
    [Fact]
    public async Task A_null_value_removes_the_row()
    {
        await _store.SetAsync("Display.Language", "en-US", SettingScopes.Tenant, null);

        await _store.SetAsync("Display.Language", null, SettingScopes.Tenant, null);

        Assert.Empty(await _store.GetAllAsync(SettingScopes.Tenant, null));
    }

    [Fact]
    public async Task Clearing_a_level_that_has_no_value_is_a_no_op()
    {
        await _store.SetAsync("Display.Language", null, SettingScopes.Tenant, null);

        Assert.Empty(await _store.GetAllAsync(SettingScopes.Tenant, null));
    }

    // 永久废弃租户时要能清干净：设置行带租户归属，租户没了它们读不到也删不掉。
    [Fact]
    public async Task RemoveAll_clears_every_scope_of_the_current_tenant()
    {
        await _store.SetAsync("Display.Language", "en-US", SettingScopes.Tenant, null);
        await _store.SetAsync("Display.Language", "ja-JP", SettingScopes.User, "user-1");
        await _store.SetAsync("Display.Language", "ko-KR", SettingScopes.User, "user-2");

        await _store.RemoveAllAsync();

        Assert.Empty(await _store.GetAllAsync(SettingScopes.Tenant, null));
        Assert.Empty(await _store.GetAllAsync(SettingScopes.User, "user-1"));
        Assert.Equal(0, await _db.Settings.CountAsync());
    }

    // 唯一约束必须在宿主级也真正生效：TenantId 与 UserId 都为 NULL 是最常用的层级之一，
    // 而 SQLite/PostgreSQL 把多个 NULL 视为互不相等，索引建在这两列上等于对它没有约束。
    // 这里直接绕开 store 插第二行，模拟并发首次写入落到同一层级同一名称。
    [Fact]
    public async Task A_duplicate_row_at_the_host_level_is_rejected_by_the_database()
    {
        await _store.SetAsync("Display.Language", "en-US", SettingScopes.Tenant, null);
        var existing = await _db.Settings.SingleAsync();

        _db.Settings.Add(new SettingRecord
        {
            ScopeKey = existing.ScopeKey,
            Name = existing.Name,
            Value = "ja-JP"
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    // 租户级默认值（TenantId 非空、UserId 为 NULL）同样是 NULL 语义会放过的组合。
    [Fact]
    public async Task A_duplicate_row_at_the_tenant_level_is_rejected_by_the_database()
    {
        var store = NewStore(Guid.CreateVersion7());
        await store.SetAsync("Display.TimeZone", "UTC", SettingScopes.Tenant, null);
        var existing = await _db.Settings.SingleAsync();

        _db.Settings.Add(new SettingRecord
        {
            TenantId = existing.TenantId,
            ScopeKey = existing.ScopeKey,
            Name = existing.Name,
            Value = "Asia/Tokyo"
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    // 同名设置在不同租户上互不干扰：ScopeKey 带租户段，否则两个租户的默认值会撞唯一索引。
    [Fact]
    public async Task The_same_setting_coexists_across_tenants()
    {
        var first = NewStore(Guid.CreateVersion7());
        var second = NewStore(Guid.CreateVersion7());

        await first.SetAsync("Display.TimeZone", "UTC", SettingScopes.Tenant, null);
        await second.SetAsync("Display.TimeZone", "Asia/Tokyo", SettingScopes.Tenant, null);

        Assert.Equal("UTC", (await first.GetAllAsync(SettingScopes.Tenant, null))["Display.TimeZone"]);
        Assert.Equal("Asia/Tokyo", (await second.GetAllAsync(SettingScopes.Tenant, null))["Display.TimeZone"]);
    }

    // 宿主行与租户行的 ScopeKey 不能相撞，否则宿主的默认值会被某个租户的写入顶掉。
    [Fact]
    public async Task Host_values_are_separate_from_tenant_values()
    {
        var tenantStore = NewStore(Guid.CreateVersion7());

        await _store.SetAsync("Display.TimeZone", "UTC", SettingScopes.Tenant, null);
        await tenantStore.SetAsync("Display.TimeZone", "Asia/Tokyo", SettingScopes.Tenant, null);

        Assert.Equal("UTC", (await _store.GetAllAsync(SettingScopes.Tenant, null))["Display.TimeZone"]);
        Assert.Equal("Asia/Tokyo", (await tenantStore.GetAllAsync(SettingScopes.Tenant, null))["Display.TimeZone"]);
    }

    // 用户标识的契约只要求非空白，任何字面量都可能是合法的用户标识。层级标记因此
    // 必须放在标识之前，否则「恰好叫那个名字」的用户会覆盖掉整个租户的默认值。
    [Theory]
    [InlineData("-")]
    [InlineData("t")]
    [InlineData("u")]
    public async Task A_user_id_cannot_collide_with_the_tenant_level(string userId)
    {
        await _store.SetAsync("Display.TimeZone", "UTC", SettingScopes.Tenant, null);
        await _store.SetAsync("Display.TimeZone", "Asia/Tokyo", SettingScopes.User, userId);

        Assert.Equal("UTC", (await _store.GetAllAsync(SettingScopes.Tenant, null))["Display.TimeZone"]);
        Assert.Equal("Asia/Tokyo", (await _store.GetAllAsync(SettingScopes.User, userId))["Display.TimeZone"]);
        Assert.Equal(2, await _db.Settings.CountAsync());
    }

    // 用户级缺标识会写出 UserId 为 null（按实体契约即租户级）而 ScopeKey 是用户级的行，
    // 两个字段各说各话。ISettingManager 挡过一道，但本契约是公开的，直接消费它也要挡住。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_user_scope_without_an_identifier_is_rejected(string? userId)
    {
        // null 抛 ArgumentNullException、空白抛 ArgumentException，两者都是 ArgumentException
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _store.GetAllAsync(SettingScopes.User, userId));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _store.SetAsync("Display.TimeZone", "UTC", SettingScopes.User, userId));
    }

    // None 与 All 不指向任何一行：静默按某一层处理会把值写到调用方没预期的地方。
    [Theory]
    [InlineData(SettingScopes.None)]
    [InlineData(SettingScopes.All)]
    public async Task A_scope_that_addresses_no_row_is_rejected(SettingScopes scope)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.GetAllAsync(scope, null));
    }

    /// <summary>进程级设置在租户上下文下读写会就地失败，而不是静默落到租户行上。</summary>
    /// <remarks>
    /// 租户上下文下宿主行会被查询过滤器滤掉（专属库形态连的还是租户自己的库），
    /// 读到空、写成租户行——两者都不报错。这条不变式必须在存储边界拦住：
    /// 上层忘了切宿主上下文时，得到的是异常而不是一个不生效的值。
    /// </remarks>
    [Fact]
    public async Task Host_scope_is_refused_outside_the_host_context()
    {
        var tenantStore = NewStore(Guid.NewGuid());

        // 解析端靠这一项决定"要不要去读宿主层"，不能靠捕获异常来判断上下文
        Assert.False(tenantStore.CanAccessHostScope);
        Assert.True(_store.CanAccessHostScope);

        await Assert.ThrowsAsync<HostScopeUnavailableException>(
            () => tenantStore.SetAsync("Logging.MinimumLevel", "Debug", SettingScopes.Host, null));
        await Assert.ThrowsAsync<HostScopeUnavailableException>(
            () => tenantStore.GetAllAsync(SettingScopes.Host, null));
    }

    // 宿主上下文下进程级与租户级共用同一行：宿主视角本就走租户层，
    // 设置名全局唯一，两者不会互相覆盖。
    [Fact]
    public async Task Host_scope_and_the_host_tenant_row_are_the_same_row()
    {
        await _store.SetAsync("Logging.MinimumLevel", "Debug", SettingScopes.Host, null);
        await _store.SetAsync("Display.Language", "zh-CN", SettingScopes.Tenant, null);

        var viaHost = await _store.GetAllAsync(SettingScopes.Host, null);
        var viaTenant = await _store.GetAllAsync(SettingScopes.Tenant, null);

        Assert.Equal("Debug", viaHost["Logging.MinimumLevel"]);
        Assert.Equal("zh-CN", viaHost["Display.Language"]);
        Assert.Equal(viaTenant, viaHost);
    }

    /// <summary>同一上下文已跟踪了旧实体时，读取仍返回数据库的当前值。</summary>
    /// <remarks>
    /// 跟踪查询遇到已跟踪的实体不会用数据库值覆盖它；写入入口的跨设置校验经这里读另一项设置，
    /// 宿主沿用同一上下文时读到旧值就会放过违序的一组。另一个上下文扮演已提交的别的请求。
    /// </remarks>
    [Fact]
    public async Task Reading_returns_the_database_value_over_an_entity_already_tracked_by_the_context()
    {
        await _store.SetAsync("Display.Language", "en-US", SettingScopes.Tenant, null);
        var tracked = await _db.Settings.SingleAsync(x => x.Name == "Display.Language");

        await using (var other = new SettingDbContext(new DbContextOptionsBuilder<SettingDbContext>().UseSqlite(_connection).Options))
        {
            var row = await other.Settings.SingleAsync(x => x.Name == "Display.Language");
            row.Value = "ja-JP";
            await other.SaveChangesAsync();
        }

        var values = await _store.GetAllAsync(SettingScopes.Tenant, null);

        Assert.Equal("ja-JP", values["Display.Language"]);
        Assert.Equal("en-US", tracked.Value);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
