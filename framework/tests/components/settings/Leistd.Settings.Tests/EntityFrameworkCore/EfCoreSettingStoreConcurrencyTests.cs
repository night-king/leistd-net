using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Leistd.Settings.Definitions;
using Leistd.Settings.EntityFrameworkCore;
using Leistd.Settings.EntityFrameworkCore.Entities;
using Leistd.Settings.EntityFrameworkCore.Stores;
using Leistd.TestBase.Doubles;
using Xunit;
using static Leistd.TestBase.Doubles.DbContextProviderFor;

namespace Leistd.Settings.Tests.EntityFrameworkCore;

/// <summary>同一层级同一名称的并发写入收敛，而不是把数据库冲突抛给调用方。</summary>
/// <remarks>
/// 共用一条 SQLite 连接，另开上下文扮演并发写入方，由拦截器在本次保存前插入它的写入，交错是确定的。
/// </remarks>
public sealed class EfCoreSettingStoreConcurrencyTests : IDisposable
{
    private const string Name = "Display.Language";

    // 测试库上的检查约束拒绝这个值，用来制造与并发无关、且落在本次条目上的保存失败
    private const string RejectedValue = "rejected";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public EfCoreSettingStoreConcurrencyTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    // 首插撞上并发方刚插入的同键行：本次写入后落库，值以它为准，且只有一行。
    [Fact]
    public async Task Concurrent_first_writes_converge_to_the_later_write()
    {
        await using var db = NewContext(new RunOnceBeforeSaveInterceptor(() => WriteAsOtherAsync("en-US")));

        await StoreOver(db).SetAsync(Name, "ja-JP", SettingScopes.Tenant, null);

        Assert.Equal("ja-JP", Assert.Single(await RowsAsync()).Value);
    }

    // 删除即“清空本键”：要删的行已被并发删除，结果一致，不算失败。
    [Fact]
    public async Task Clearing_a_value_removed_concurrently_succeeds()
    {
        await WriteAsOtherAsync("en-US");
        await using var db = NewContext(new RunOnceBeforeSaveInterceptor(() => WriteAsOtherAsync(null)));

        await StoreOver(db).SetAsync(Name, null, SettingScopes.Tenant, null);

        Assert.Empty(await RowsAsync());
    }

    // 要改的行已被并发删除：本次写入后落库，值重新插入。
    [Fact]
    public async Task Updating_a_value_removed_concurrently_writes_it_again()
    {
        await WriteAsOtherAsync("en-US");
        await using var db = NewContext(new RunOnceBeforeSaveInterceptor(() => WriteAsOtherAsync(null)));

        await StoreOver(db).SetAsync(Name, "ja-JP", SettingScopes.Tenant, null);

        Assert.Equal("ja-JP", Assert.Single(await RowsAsync()).Value);
    }

    // 删除期间同键被删后重建（新主键）：清空本键也覆盖这一行。
    [Fact]
    public async Task Clearing_also_removes_a_row_recreated_concurrently()
    {
        await WriteAsOtherAsync("en-US");
        await using var db = NewContext(new RunOnceBeforeSaveInterceptor(async () =>
        {
            await WriteAsOtherAsync(null);
            await WriteAsOtherAsync("ko-KR");
        }));

        await StoreOver(db).SetAsync(Name, null, SettingScopes.Tenant, null);

        Assert.Empty(await RowsAsync());
    }

    // 恢复只撤下本次条目：宿主在同一上下文、同一事务里的其他待写实体照常提交。
    [Fact]
    public async Task Recovery_keeps_the_hosts_other_pending_changes_in_the_transaction()
    {
        DbTransaction? outer = null;
        await using var db = NewContext(new RunOnceBeforeSaveInterceptor(() => WriteAsOtherAsync("en-US", outer)));
        await using var transaction = await db.Database.BeginTransactionAsync();
        outer = transaction.GetDbTransaction();

        db.Notes.Add(new HostNote { Code = "pending" });
        await StoreOver(db).SetAsync(Name, "ja-JP", SettingScopes.Tenant, null);
        await transaction.CommitAsync();

        Assert.Equal("ja-JP", Assert.Single(await RowsAsync()).Value);
        await using var check = NewContext();
        Assert.Equal("pending", Assert.Single(await check.Notes.ToListAsync()).Code);
    }

    // 同次保存里别的实体失败不是本键的并发冲突：原样抛出，且不能为了让本次写入通过而撤掉宿主的待写实体。
    [Theory]
    [InlineData(null, "ja-JP")]
    [InlineData("en-US", "ja-JP")]
    [InlineData("en-US", null)]
    public async Task A_failure_of_another_entity_is_rethrown(string? existing, string? value)
    {
        if (existing is not null)
        {
            await WriteAsOtherAsync(existing);
        }

        await using (var seed = NewContext())
        {
            seed.Notes.Add(new HostNote { Code = "taken" });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var duplicate = db.Notes.Add(new HostNote { Code = "taken" });

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => StoreOver(db).SetAsync(Name, value, SettingScopes.Tenant, null));

        Assert.Equal(EntityState.Added, duplicate.State);
        Assert.Equal(existing, (await RowsAsync()).SingleOrDefault()?.Value);
    }

    // 首插失败而回库确认不到同键行：不是并发首写，原样抛出，不再插第二次。
    [Fact]
    public async Task A_failed_first_write_without_a_concurrent_row_is_not_retried()
    {
        var saves = new SaveCounter();
        await using var db = NewContext(saves);

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => StoreOver(db).SetAsync(Name, RejectedValue, SettingScopes.Tenant, null));

        Assert.Equal(1, saves.Count);
        Assert.Empty(await RowsAsync());
    }

    // 只重试一次：重试时再次冲突（赢家行又被删）原样抛出，不无限追赶。
    [Fact]
    public async Task A_second_conflict_is_rethrown()
    {
        await using var db = NewContext(new BeforeEachSaveInterceptor(
            () => WriteAsOtherAsync("en-US"),
            () => WriteAsOtherAsync(null)));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => StoreOver(db).SetAsync(Name, "ja-JP", SettingScopes.Tenant, null));
    }

    // 恢复途中取消照常抛出，不被当作冲突吞掉，也不再写入。
    [Fact]
    public async Task Cancellation_during_recovery_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await using var db = NewContext(
            new RunOnceBeforeSaveInterceptor(() => WriteAsOtherAsync("en-US")),
            new CancelOnSaveFailedInterceptor(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StoreOver(db).SetAsync(Name, "ja-JP", SettingScopes.Tenant, null, cancellation.Token));

        Assert.Equal("en-US", Assert.Single(await RowsAsync()).Value);
    }

    private ConcurrencyDbContext NewContext(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<ConcurrencyDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options);

    private static EfCoreSettingStore<ConcurrencyDbContext> StoreOver(ConcurrencyDbContext db)
        => new(Fixed(db), new FakeCurrentTenant(null));

    // 并发写入方：独立上下文，经同一个存储写入；有外层事务时加入它（SQLite 同一连接只能有一个事务）。
    private async Task WriteAsOtherAsync(string? value, DbTransaction? transaction = null)
    {
        await using var other = NewContext();
        if (transaction is not null)
        {
            await other.Database.UseTransactionAsync(transaction);
        }

        await StoreOver(other).SetAsync(Name, value, SettingScopes.Tenant, null);
    }

    private async Task<List<SettingRecord>> RowsAsync()
    {
        await using var db = NewContext();
        return await db.Settings.AsNoTracking().ToListAsync();
    }

    public void Dispose() => _connection.Dispose();

    public sealed class ConcurrencyDbContext(DbContextOptions<ConcurrencyDbContext> options) : DbContext(options)
    {
        public DbSet<SettingRecord> Settings => Set<SettingRecord>();

        public DbSet<HostNote> Notes => Set<HostNote>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureSettings();
            modelBuilder.Entity<SettingRecord>().ToTable(table =>
                table.HasCheckConstraint("CK_Settings_NotRejected", $"Value <> '{RejectedValue}'"));
            modelBuilder.Entity<HostNote>().HasIndex(x => x.Code).IsUnique();
        }
    }

    /// <summary>宿主自己的实体，与设置写入同处一个上下文。</summary>
    public sealed class HostNote
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Code { get; set; } = default!;
    }

    /// <summary>第 N 次 SaveChanges 之前执行第 N 个动作，用于让重试再撞一次冲突。</summary>
    private sealed class BeforeEachSaveInterceptor(params Func<Task>[] actions) : SaveChangesInterceptor
    {
        private int _saves;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_saves < actions.Length)
            {
                await actions[_saves++]();
            }

            return result;
        }
    }

    /// <summary>统计 SaveChanges 的调用次数。</summary>
    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>保存失败时取消令牌，使取消落在冲突恢复途中。</summary>
    private sealed class CancelOnSaveFailedInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
            => cancellation.CancelAsync();
    }
}
