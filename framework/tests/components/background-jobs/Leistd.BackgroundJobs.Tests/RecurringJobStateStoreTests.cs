using Leistd.BackgroundJobs.EntityFrameworkCore;
using Leistd.BackgroundJobs.EntityFrameworkCore.Stores;
using Leistd.TestBase.Doubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.BackgroundJobs.Tests;

/// <summary>共享水位：多副本读到同一个"已完成时段"，且水位只前进。</summary>
public sealed class RecurringJobStateStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly StateDbContext _db;
    private readonly EfCoreRecurringJobStateStore<StateDbContext> _store;

    public RecurringJobStateStoreTests()
    {
        _connection.Open();
        _db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _store = new EfCoreRecurringJobStateStore<StateDbContext>(new FixedDbContextProvider<StateDbContext>(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_recorded_slot_round_trips_as_utc()
    {
        var slot = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);

        Assert.Null(await _store.GetLastCompletedSlotAsync("job"));
        await _store.SetLastCompletedSlotAsync("job", slot);

        Assert.Equal(slot, await _store.GetLastCompletedSlotAsync("job"));
    }

    /// <summary>较早的时段晚到（跨副本时钟差）不能把已完成的更晚时段覆盖回去，否则那个更晚的时段会被重做。</summary>
    [Fact]
    public async Task The_watermark_only_moves_forward()
    {
        var later = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
        await _store.SetLastCompletedSlotAsync("job", later);
        await _store.SetLastCompletedSlotAsync("job", later.AddDays(-1));

        Assert.Equal(later, await _store.GetLastCompletedSlotAsync("job"));
    }

    private sealed class StateDbContext(DbContextOptions<StateDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureBackgroundJobs();
    }
}
