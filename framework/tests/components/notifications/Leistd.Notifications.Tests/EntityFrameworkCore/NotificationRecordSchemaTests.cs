using Leistd.Notifications.EntityFrameworkCore;
using Leistd.Notifications.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Leistd.Notifications.Tests.EntityFrameworkCore;

/// <summary>通知表的模型配置：长度约束与查询索引。</summary>
/// <remarks>
/// 用 SQLite 而不是 InMemory：长度约束与复合索引只有真正建库时才产生 DDL，
/// InMemory 全内存求值会让整个 <c>NotificationRecordConfiguration</c> 静默通过。
/// </remarks>
public class NotificationRecordSchemaTests(NotificationSchemaFixture fixture)
    : IClassFixture<NotificationSchemaFixture>
{
    public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
    {
        public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureNotifications();
    }

    private sealed class RenamedNotificationDbContext(DbContextOptions<RenamedNotificationDbContext> options)
        : DbContext(options)
    {
        public DbSet<NotificationRecord> UserInbox => Set<NotificationRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureNotifications();
    }

    private SqliteConnection _connection => fixture.Connection;
    private NotificationDbContext _db => fixture.Db;

    private static NotificationRecord NewRecord(string userId = "u1") => new()
    {
        UserId = userId,
        Title = "t",
        Type = "System",
        CreationTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>表名跟着宿主的 <c>DbSet</c> 属性名走，组件不写死它。</summary>
    /// <remarks>
    /// 用两个 DbSet 名不同的上下文对照：组件若调了 <c>ToTable("...")</c>，两边会得到同一个名字。
    /// 单看一个上下文分辨不出"没写死"和"写死成刚好一样"。
    /// 写死表名会给宿主库打上框架前缀，而宿主改不动。
    /// </remarks>
    [Fact]
    public void Table_name_follows_the_host_dbset_instead_of_being_pinned()
    {
        Assert.Equal(
            nameof(NotificationDbContext.Notifications),
            _db.Model.FindEntityType(typeof(NotificationRecord))!.GetTableName());

        using var renamed = new RenamedNotificationDbContext(
            new DbContextOptionsBuilder<RenamedNotificationDbContext>()
                .UseSqlite(_connection).Options);

        Assert.Equal(
            nameof(RenamedNotificationDbContext.UserInbox),
            renamed.Model.FindEntityType(typeof(NotificationRecord))!.GetTableName());
    }

    [Theory]
    [InlineData(nameof(NotificationRecord.UserId), 128, false)]
    [InlineData(nameof(NotificationRecord.Title), 256, false)]
    [InlineData(nameof(NotificationRecord.Content), 2000, true)]
    [InlineData(nameof(NotificationRecord.Type), 64, false)]
    [InlineData(nameof(NotificationRecord.Link), 512, true)]
    [InlineData(nameof(NotificationRecord.Icon), 64, true)]
    [InlineData(nameof(NotificationRecord.RelatedEntityId), 128, true)]
    [InlineData(nameof(NotificationRecord.RelatedEntityType), 64, true)]
    [InlineData(nameof(NotificationRecord.CreatorId), 64, true)]
    public void String_columns_declare_their_length_and_nullability(string name, int maxLength, bool nullable)
    {
        var property = _db.Model.FindEntityType(typeof(NotificationRecord))!.FindProperty(name)!;

        Assert.Equal(maxLength, property.GetMaxLength());
        Assert.Equal(nullable, property.IsNullable);
    }

    // 未声明长度的字符串在关系型库上会建成无界文本列，无法建索引也无法预估存储。
    // MetadataJson 是唯一刻意无界的（扩展元数据），这里把它与"忘了写长度"区分开。
    [Fact]
    public void Metadata_is_the_only_unbounded_string_column()
    {
        var unbounded = _db.Model.FindEntityType(typeof(NotificationRecord))!
            .GetProperties()
            .Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is null)
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal([nameof(NotificationRecord.MetadataJson)], unbounded);
    }

    // 三条索引对应三个访问路径：拉列表、数未读、保留期整库清理。少一条就是全表扫描。
    // 清理那条尤其容易被漏：它不带 UserId（IgnoreQueryFilters 整库扫），前两条都以 UserId
    // 打头，用不上——而清理是唯一会随表增长越跑越慢的那个。
    [Fact]
    public void Query_indexes_cover_listing_unread_count_and_retention()
    {
        var indexes = _db.Model.FindEntityType(typeof(NotificationRecord))!
            .GetIndexes()
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToArray();

        Assert.Contains($"{nameof(NotificationRecord.UserId)},{nameof(NotificationRecord.CreationTime)}", indexes);
        Assert.Contains($"{nameof(NotificationRecord.UserId)},{nameof(NotificationRecord.IsRead)}", indexes);
        Assert.Contains(nameof(NotificationRecord.CreationTime), indexes);
    }

    [Fact]
    public void Primary_key_is_the_id()
    {
        var key = _db.Model.FindEntityType(typeof(NotificationRecord))!.FindPrimaryKey()!;

        Assert.Equal([nameof(NotificationRecord.Id)], key.Properties.Select(p => p.Name));
    }

    // 必填列真正进了 DDL：SQLite 会执行 NOT NULL。
    // （长度约束 SQLite 不强制，因此长度只能在上面按模型元数据断言；
    //  换成 PostgreSQL/SQL Server 时那条 DDL 才会真正拒绝超长值。）
    [Fact]
    public async Task Required_columns_are_enforced_by_the_database()
    {
        var record = NewRecord();
        record.Title = null!;
        _db.Add(record);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());

        // 失败的实体留在跟踪器里会污染同类后续用例的保存
        _db.ChangeTracker.Clear();
    }

    // 共享库意味着这里不能用 SingleAsync：按自己的主键取回，与同类其它用例的写入互不干扰。
    [Fact]
    public async Task A_valid_record_round_trips()
    {
        var record = NewRecord($"u-{Guid.NewGuid():N}");
        _db.Add(record);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var loaded = await _db.Notifications.SingleAsync(x => x.Id == record.Id);

        Assert.Equal(record.UserId, loaded.UserId);
    }
}

/// <summary>整类共享一次 SQLite 建库。</summary>
/// <remarks>
/// EnsureCreated 是这一类用例的成本大头，而绝大多数断言只读模型元数据。
/// 按测试方法各建一次库，付的是同一份 DDL 的 N 倍。
/// </remarks>
public sealed class NotificationSchemaFixture : IDisposable
{
    public SqliteConnection Connection { get; } = new("Filename=:memory:");
    public NotificationRecordSchemaTests.NotificationDbContext Db { get; }

    public NotificationSchemaFixture()
    {
        Connection.Open();
        Db = new NotificationRecordSchemaTests.NotificationDbContext(
            new DbContextOptionsBuilder<NotificationRecordSchemaTests.NotificationDbContext>()
                .UseSqlite(Connection).Options);
        Db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Db.Dispose();
        Connection.Dispose();
    }
}
