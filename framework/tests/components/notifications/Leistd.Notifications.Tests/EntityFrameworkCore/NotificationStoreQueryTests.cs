using Leistd.Data.Paging;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.EntityFrameworkCore;
using Leistd.Notifications.EntityFrameworkCore.Stores;
using Leistd.TestBase.Doubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.Notifications.Tests.EntityFrameworkCore;

/// <summary>用户通知的分页、仅未读与删除：只作用于收件人自己的通知。</summary>
public sealed class NotificationStoreQueryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly StoreDbContext _db;
    private readonly EfCoreNotificationStore<StoreDbContext> _store;

    public NotificationStoreQueryTests()
    {
        _connection.Open();
        _db = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _store = new EfCoreNotificationStore<StoreDbContext>(new FixedDbContextProvider<StoreDbContext>(_db), new FakeClock());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<NotificationOutputDto> SaveAsync(string userId, int minute, bool isRead = false)
    {
        var notification = new NotificationOutputDto
        {
            Id = Guid.CreateVersion7().ToString(),
            Title = $"n{minute}",
            Type = NotificationInputDto.DefaultType,
            IsRead = isRead,
            CreationTime = new DateTime(2026, 9, 20, 10, minute, 0, DateTimeKind.Utc)
        };
        await _store.SaveAsync(notification, userId);
        return notification;
    }

    [Fact]
    public async Task Pages_are_newest_first_and_can_be_limited_to_unread()
    {
        await SaveAsync("u1", 1, isRead: true);
        await SaveAsync("u1", 2);
        await SaveAsync("u1", 3);
        await SaveAsync("u2", 4);

        var page = await _store.GetByUserAsync("u1", new PageRequest { Limit = 2 });
        var unread = await _store.GetByUserAsync("u1", new PageRequest(), unreadOnly: true);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(["n3", "n2"], page.Items.Select(n => n.Title));
        Assert.Equal(["n3", "n2"], unread.Items.Select(n => n.Title));
    }

    /// <summary>删除只作用于收件人自己的通知：拿到别人通知的 ID 也删不掉。</summary>
    [Fact]
    public async Task A_user_can_only_delete_their_own_notifications()
    {
        var mine = await SaveAsync("u1", 1);
        var theirs = await SaveAsync("u2", 2);

        Assert.False(await _store.DeleteAsync(theirs.Id, "u1"));
        Assert.True(await _store.DeleteAsync(mine.Id, "u1"));
        Assert.Equal(0, (await _store.GetByUserAsync("u1", new PageRequest())).TotalCount);
        Assert.Equal(1, (await _store.GetByUserAsync("u2", new PageRequest())).TotalCount);
    }

    [Fact]
    public async Task Deleting_all_removes_only_that_users_notifications()
    {
        await SaveAsync("u1", 1);
        await SaveAsync("u1", 2);
        await SaveAsync("u2", 3);

        Assert.Equal(2, await _store.DeleteAllAsync("u1"));
        Assert.Equal(1, (await _store.GetByUserAsync("u2", new PageRequest())).TotalCount);
    }

    private sealed class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureNotifications();
    }
}
