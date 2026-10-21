using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Infrastructure.EventBus;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.UnitOfWork;
using Leistd.EventBus.Local;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Leistd.EventBus.Abstractions;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>领域对象 AddLocalEvent → 仓储/DbContext SaveChanges → 本地事件被发布到 handler。</summary>
/// <remarks>
/// 收集时机：若在 SavedChanges（保存后实体已 Unchanged）收集，会收集到 0 个事件、handler 不触发。
/// 此测试走真实 SaveChanges 拦截器链路（非直接 PublishAsync），覆盖业务真实路径。
/// </remarks>
public class LocalEventInterceptorTests
{
    private sealed class TestEntity : Entity<Guid>
    {
        public string Name { get; private set; } = "";
        private TestEntity() { }
        public TestEntity(string name) : this(name, Guid.NewGuid()) { }

        public TestEntity(string name, Guid id)
        {
            Id = id;
            Name = name;
            AddLocalEvent(new TestCreatedEvent(Name));
        }

        // 只登记事件、不改任何属性：保存时实体保持 Unchanged
        public void Announce() => AddLocalEvent(new TestCreatedEvent(Name));
    }

    private sealed class TestCreatedEvent(string name) : LocalEvent
    {
        public string Name { get; } = name;
    }

    private sealed class TestDbContext(DbContextOptions options) : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<TestEntity> Items => Set<TestEntity>();
        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name);
            });
        }
    }

    private sealed class TestHandler : IEventHandler<TestCreatedEvent>
    {
        public static int Invoked;
        public static string? LastName;
        public Task HandleAsync(TestCreatedEvent @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Invoked);
            LastName = @event.Name;
            return Task.CompletedTask;
        }
    }

    private static (TestDbContext db, ServiceProvider sp) Build()
    {
        TestHandler.Invoked = 0;
        TestHandler.LastName = null;

        // 不开工作单元：管理器的 Current 为空，事件在保存成功后立即发布
        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalEventBus()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddUnitOfWork()
            .AddScoped<IEventHandler<TestCreatedEvent>, TestHandler>()
            .BuildServiceProvider();

        var interceptor = new LocalEventSaveChangesInterceptor(
            sp.GetRequiredService<ILocalEventBus>(),
            sp.GetRequiredService<IUnitOfWorkManager>(),
            NullLogger<LocalEventSaveChangesInterceptor>.Instance);

        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase($"evt-{Guid.NewGuid()}")
            .AddInterceptors(interceptor)
            .Options;

        return (new TestDbContext(options), sp);
    }

    [Fact]
    public async Task SaveChangesAsync_publishes_local_events_to_handler()
    {
        var (db, sp) = Build();
        using var _ = sp;

        db.Items.Add(new TestEntity("created-via-savechanges"));
        await db.SaveChangesAsync();

        Assert.Equal(1, TestHandler.Invoked);
        Assert.Equal("created-via-savechanges", TestHandler.LastName);
    }

    [Fact]
    public void SaveChanges_sync_publishes_local_events_to_handler()
    {
        var (db, sp) = Build();
        using var _ = sp;

        db.Items.Add(new TestEntity("created-sync"));
        db.SaveChanges();

        Assert.Equal(1, TestHandler.Invoked);
    }

    /// <summary>实体本身未变更时，它登记的事件也要发布。</summary>
    /// <remarks>
    /// 聚合根只改了子实体或只登记了事件时，根实体在保存时是 Unchanged。
    /// 收集若只看 Added/Modified/Deleted，而清空覆盖全部实体，这些事件会被清掉却不发布，且没有任何报错。
    /// </remarks>
    [Fact]
    public async Task Events_raised_by_an_unchanged_entity_are_published()
    {
        var (db, sp) = Build();
        using var _ = sp;
        var root = new TestEntity("root");
        db.Items.Add(root);
        await db.SaveChangesAsync();

        root.Announce();
        db.Items.Add(new TestEntity("child"));
        await db.SaveChangesAsync();

        Assert.Equal("Unchanged", db.Entry(root).State.ToString());
        Assert.Equal(3, TestHandler.Invoked);
    }

    // 聚合根只登记事件、上下文没有任何数据变更（影响 0 行）时，EF 仍经过保存拦截器，事件同样要发布
    [Fact]
    public async Task Events_raised_without_any_data_change_are_published()
    {
        var (db, sp) = Build();
        using var _ = sp;
        var root = new TestEntity("root");
        db.Items.Add(root);
        await db.SaveChangesAsync();

        root.Announce();
        var affected = await db.SaveChangesAsync();

        Assert.Equal(0, affected);
        Assert.Equal(2, TestHandler.Invoked);
    }

    /// <summary>保存失败时已收集的领域事件必须丢弃。</summary>
    /// <remarks>
    /// <para>事件按 DbContext 暂存、在 <c>SavedChanges</c> 才发布。失败后若不清空，
    /// 同一个上下文的下一次成功保存会把上一次失败的事件一起发出去——
    /// 业务侧收到一个从未发生过的领域事件，且与当次操作毫无关系。</para>
    /// <para>用 SQLite 制造真实的主键冲突，而不是把实体状态改成不可能的组合：
    /// 失败必须发生在拦截器收集之后、发布之前，构造出来的假状态保证不了这个时序。</para>
    /// </remarks>
    [Fact]
    public async Task Failed_save_discards_the_events_it_had_collected()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var (db, sp) = BuildOn(connection);
        using (db)
        using (sp)
        {
            var duplicated = Guid.NewGuid();
            db.Add(new TestEntity("first", duplicated));
            await db.SaveChangesAsync();
            Assert.Equal(1, TestHandler.Invoked);

            db.ChangeTracker.Clear();
            db.Add(new TestEntity("conflict", duplicated));
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());

            // 失败这一次不得发布
            Assert.Equal(1, TestHandler.Invoked);

            db.ChangeTracker.Clear();
            db.Add(new TestEntity("third", Guid.NewGuid()));
            await db.SaveChangesAsync();

            // 只多了属于这次成功保存的那一个，失败那次的没有被顺带发出
            Assert.Equal(2, TestHandler.Invoked);
            Assert.Equal("third", TestHandler.LastName);
        }
    }

    // 同步失败入口与异步走同一段丢弃逻辑，两条都要钉——只覆盖一条时另一条改坏不会红。
    [Fact]
    public void Failed_sync_save_also_discards_collected_events()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var (db, sp) = BuildOn(connection);
        using (db)
        using (sp)
        {
            var duplicated = Guid.NewGuid();
            db.Add(new TestEntity("first", duplicated));
            db.SaveChanges();

            db.ChangeTracker.Clear();
            db.Add(new TestEntity("conflict", duplicated));
            Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());

            db.ChangeTracker.Clear();
            db.Add(new TestEntity("third", Guid.NewGuid()));
            db.SaveChanges();

            Assert.Equal(2, TestHandler.Invoked);
        }
    }

    private static (TestDbContext db, ServiceProvider sp) BuildOn(SqliteConnection connection)
    {
        TestHandler.Invoked = 0;
        TestHandler.LastName = null;

        // 不开工作单元：管理器的 Current 为空，事件在保存成功后立即发布
        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalEventBus()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddUnitOfWork()
            .AddScoped<IEventHandler<TestCreatedEvent>, TestHandler>()
            .BuildServiceProvider();

        var interceptor = new LocalEventSaveChangesInterceptor(
            sp.GetRequiredService<ILocalEventBus>(),
            sp.GetRequiredService<IUnitOfWorkManager>(),
            NullLogger<LocalEventSaveChangesInterceptor>.Instance);

        var db = new TestDbContext(new DbContextOptionsBuilder()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options);
        db.Database.EnsureCreated();
        return (db, sp);
    }
}
