using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary><c>AddDddDbContext</c> 一次挂齐保存时刻的三项能力，且经宿主真实接线生效。</summary>
/// <remarks>
/// <para>本测试刻意<b>不手工 new 拦截器</b>，而是走
/// <c>AddDddInfrastructure()</c> → <c>AddDbContext&lt;T&gt;(...)</c> → <c>AddDddDbContext&lt;T&gt;()</c>
/// 这条宿主真实路径。手工构造拦截器的用例
/// <b>证明不了宿主接线是否真的挂上了它</b>：拦截器只注册为可解析服务、
/// 却没挂到 DbContext 上时，<c>IHasConcurrencyStamp</c> 静默不生效。</para>
/// <para>三项能力在同一次保存里一起断言：任何一个从接线里掉出去都会红。</para>
/// </remarks>
public sealed class DddInterceptorWiringTests : IAsyncLifetime
{
    private SqliteConnection _connection = default!;
    private ServiceProvider _services = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // 最小组合：不单独注册事件总线。领域事件总线随 AddDddInfrastructure 注册，
        // 缺它时解析 DbContext 即抛，宿主不必另调 AddLocalEventBus
        services.AddDddInfrastructure();
        // 一个实现只注册一次，接口做别名转发。分两条 AddSingleton 会得到两个实例，
        // 处理器加的计数与断言读的计数就不是同一个对象——与 Redis 锁那处重复注册同一类错误
        services.AddSingleton<RenameRecorder>();
        services.AddSingleton<IEventHandler<DocumentRenamed>>(sp => sp.GetRequiredService<RenameRecorder>());

        // 宿主形态：AddDbContext 只管连接，登记上下文时挂齐三个拦截器
        services.AddDbContext<WiringDbContext>(options => options.UseSqlite(_connection));
        services.AddDddDbContext<WiringDbContext>();

        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WiringDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task All_three_save_time_concerns_fire_through_the_single_entry_point()
    {
        Guid documentId;
        string stampAfterInsert;

        using (var scope = _services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WiringDbContext>();
            var document = new Document { Title = "v1" };
            dbContext.Documents.Add(document);
            await dbContext.SaveChangesAsync();

            documentId = document.Id;
            stampAfterInsert = document.ConcurrencyStamp;
        }

        using (var scope = _services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WiringDbContext>();
            var document = await dbContext.Documents.SingleAsync(x => x.Id == documentId);

            document.Rename("v2");
            await dbContext.SaveChangesAsync();

            // 1) 并发标记：领域代码没调用任何换发方法，标记必须已更换
            Assert.NotEqual(stampAfterInsert, document.ConcurrencyStamp);

            // 2) 修改审计：保存时刻填充
            Assert.NotNull(document.LastModificationTime);
        }

        // 3) 领域事件：保存成功后发布
        Assert.Equal(1, _services.GetRequiredService<RenameRecorder>().Count);
    }

    // 同步保存是另一个入口，走的是拦截器里另一组重写方法。
    // 宿主的种子数据、迁移作业里普遍用它，只覆盖异步时这条路径改坏不会红。
    [Fact]
    public void Synchronous_save_goes_through_the_same_interceptors()
    {
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WiringDbContext>();

        var document = new Document { Title = "sync-v1" };
        dbContext.Documents.Add(document);
        dbContext.SaveChanges();
        var stampAfterInsert = document.ConcurrencyStamp;

        document.Rename("sync-v2");
        dbContext.SaveChanges();

        Assert.NotEqual(stampAfterInsert, document.ConcurrencyStamp);
        Assert.NotNull(document.LastModificationTime);
    }

    [Fact]
    public async Task A_concurrent_write_conflicts_under_host_wiring()
    {
        // 端到端确认：不是"拦截器类型可用"，而是"宿主这样接线后并发保护真的生效"
        Guid documentId;
        using (var scope = _services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WiringDbContext>();
            var document = new Document { Title = "seed" };
            dbContext.Documents.Add(document);
            await dbContext.SaveChangesAsync();
            documentId = document.Id;
        }

        using var firstScope = _services.CreateScope();
        using var secondScope = _services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<WiringDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<WiringDbContext>();

        var byFirst = await first.Documents.SingleAsync(x => x.Id == documentId);
        var bySecond = await second.Documents.SingleAsync(x => x.Id == documentId);

        byFirst.Rename("by-first");
        await first.SaveChangesAsync();

        bySecond.Rename("by-second");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }


    private sealed class DocumentRenamed(string title) : LocalEvent
    {
        public string Title { get; } = title;
    }

    private sealed class RenameRecorder : IEventHandler<DocumentRenamed>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task HandleAsync(DocumentRenamed @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    private sealed class Document : FullAuditedEntity<Guid>, IAggregateRoot<Guid>, IHasConcurrencyStamp
    {
        public Document() => Id = Guid.CreateVersion7();

        public string Title { get; set; } = string.Empty;

        public string ConcurrencyStamp { get; set; } = ConcurrencyStamps.New();

        public void Rename(string title)
        {
            // 领域方法只改业务状态；标记换发与审计填充都由基础设施承担
            Title = title;
            AddLocalEvent(new DocumentRenamed(title));
        }
    }

    private sealed class WiringDbContext(DbContextOptions<WiringDbContext> options, IServiceProvider serviceProvider)
        : BaseDbContext(options, serviceProvider)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Document>(b =>
            {
                b.HasKey(x => x.Id);
            });
    }
}
