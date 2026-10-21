using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.Ddd.Domain.Values;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.Ddd.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

using Xunit;
using Leistd.Auditing.Abstractions;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>DDD 建模原语：值对象相等性、聚合根标记、并发标记的模型配置。</summary>
public class DomainPrimitiveTests
{
    private sealed class Money(decimal amount, string currency) : ValueObject
    {
        public decimal Amount { get; } = amount;

        // 派生字段：规范化后的取值参与相等性，原始输入不参与
        public string Currency { get; } = currency.ToUpperInvariant();

        protected override IEnumerable<object?> GetAtomicValues()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    private sealed class DiscountedMoney(decimal amount, string currency) : ValueObject
    {
        public decimal Amount { get; } = amount;
        public string Currency { get; } = currency.ToUpperInvariant();

        protected override IEnumerable<object?> GetAtomicValues()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    [Fact]
    public void Value_objects_are_equal_when_their_atomic_values_are_equal()
    {
        var left = new Money(9.9m, "usd");
        var right = new Money(9.9m, "USD");

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Value_objects_of_different_types_are_never_equal()
    {
        // 分量完全相同但类型不同：Money 与 DiscountedMoney 不是同一个值，
        // 否则"打折后的金额"会被当成"原价"参与去重与字典查找
        var money = new Money(9.9m, "USD");
        var discounted = new DiscountedMoney(9.9m, "USD");

        Assert.NotEqual<object>(money, discounted);
        Assert.False(money.Equals(discounted));
    }

    [Fact]
    public void Value_object_equality_handles_null()
    {
        Money? left = null;
        Money? right = null;

        Assert.True(left == right);
        Assert.False(left != right);
        Assert.False(new Money(1m, "USD") == null);
        Assert.False(null == new Money(1m, "USD"));
    }

    [Fact]
    public void Value_objects_differing_in_one_component_are_not_equal()
    {
        Assert.NotEqual(new Money(9.9m, "USD"), new Money(9.9m, "EUR"));
        Assert.NotEqual(new Money(9.9m, "USD"), new Money(10m, "USD"));
    }


    private sealed class Order : FullAuditedEntity<Guid>, IAggregateRoot<Guid>
    {
        public string Code { get; set; } = string.Empty;
    }

    [Fact]
    public void An_aggregate_root_can_still_pick_its_auditing_base_class()
    {
        // 聚合根做成接口而不是基类的全部意义：Order 同时是"带完整审计的实体"和"聚合根"，
        // 不需要框架维护一套并行的 FullAuditedAggregateRoot 类型（那必须复制审计属性）
        var order = new Order();

        Assert.IsAssignableFrom<IAggregateRoot>(order);
        Assert.IsAssignableFrom<IAggregateRoot<Guid>>(order);
        Assert.IsAssignableFrom<IFullAuditedObject>(order);
        Assert.IsAssignableFrom<IEntity<Guid>>(order);
    }


    private sealed class Document : Entity<Guid>, IHasConcurrencyStamp
    {
        public string Title { get; set; } = string.Empty;
        public string ConcurrencyStamp { get; set; } = ConcurrencyStamps.New();
    }

    private sealed class PrimitiveDbContext(DbContextOptions<PrimitiveDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Document>(b =>
            {
                b.HasKey(x => x.Id);
            });
        }
    }

    [Fact]
    public void The_entity_convention_marks_the_concurrency_stamp_as_a_token()
    {
        var options = new DbContextOptionsBuilder<PrimitiveDbContext>()
            .UseInMemoryDatabase($"primitives-{Guid.NewGuid()}")
            .Options;

        using var dbContext = new PrimitiveDbContext(options);

        var property = dbContext.Model
            .FindEntityType(typeof(Document))!
            .FindProperty(nameof(IHasConcurrencyStamp.ConcurrencyStamp))!;

        Assert.True(property.IsConcurrencyToken);
        Assert.Equal(40, property.GetMaxLength());

        // 必填使每一行都带标记参与并发比较
        Assert.False(property.IsNullable);
    }

    private static PrimitiveDbContext CreateSqliteContext(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<PrimitiveDbContext>()
            .UseSqlite(connection)
            // 并发标记换发由基础设施承担；宿主按需挂载，与其它 SaveChanges 拦截器同口径
            .AddInterceptors(new ConcurrencyStampSaveChangesInterceptor())
            .Options;

        return new PrimitiveDbContext(options);
    }

    [Fact]
    public async Task A_modified_entity_gets_a_new_concurrency_stamp_automatically()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using var dbContext = CreateSqliteContext(connection);
        await dbContext.Database.EnsureCreatedAsync();

        var document = new Document { Title = "v1" };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync();
        var initial = document.ConcurrencyStamp;

        document.Title = "v2";
        await dbContext.SaveChangesAsync();

        // 领域代码没有调用任何换发方法：标记必须已经变了
        Assert.NotEqual(initial, document.ConcurrencyStamp);
    }

    [Fact]
    public async Task A_concurrent_write_loses_instead_of_silently_overwriting()
    {
        // 并发保护必须真的生效——本测试全程不调用任何换发方法。
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using var seed = CreateSqliteContext(connection);
        await seed.Database.EnsureCreatedAsync();
        var seeded = new Document { Title = "v1" };
        seed.Documents.Add(seeded);
        await seed.SaveChangesAsync();

        using var first = CreateSqliteContext(connection);
        using var second = CreateSqliteContext(connection);

        var byFirst = await first.Documents.SingleAsync(x => x.Id == seeded.Id);
        var bySecond = await second.Documents.SingleAsync(x => x.Id == seeded.Id);

        byFirst.Title = "by-first";
        await first.SaveChangesAsync();

        bySecond.Title = "by-second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        // 赢家的修改保留下来
        using var verify = CreateSqliteContext(connection);
        Assert.Equal("by-first", (await verify.Documents.SingleAsync(x => x.Id == seeded.Id)).Title);
    }

    [Fact]
    public void A_new_stamp_is_non_empty_and_unique()
    {
        Assert.NotEqual(ConcurrencyStamps.New(), ConcurrencyStamps.New());
        Assert.NotEmpty(ConcurrencyStamps.New());
    }

    /// <summary>漏写初值的实体：插入前由拦截器补种，列不会带着空值落库</summary>
    private sealed class Unstamped : Entity<Guid>, IHasConcurrencyStamp
    {
        public string Title { get; set; } = string.Empty;

        // 刻意不给初值——正是要验证的那种写法
        public string ConcurrencyStamp { get; set; } = string.Empty;
    }

    private sealed class UnstampedDbContext(DbContextOptions<UnstampedDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<Unstamped> Items => Set<Unstamped>();

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Unstamped>(b =>
            {
                b.HasKey(x => x.Id);
            });
        }
    }

    [Fact]
    public async Task An_entity_inserted_without_a_stamp_gets_one_seeded()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<UnstampedDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new ConcurrencyStampSaveChangesInterceptor())
            .Options;

        using var dbContext = new UnstampedDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var item = new Unstamped { Title = "v1" };
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        Assert.False(string.IsNullOrWhiteSpace(item.ConcurrencyStamp));
    }
}
