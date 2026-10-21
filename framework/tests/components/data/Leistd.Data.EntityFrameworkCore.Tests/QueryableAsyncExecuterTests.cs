using Leistd.Data.EntityFrameworkCore.Querying;
using Leistd.Data.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.Data.EntityFrameworkCore.Tests;

/// <summary>Domain 层拿到 <see cref="IQueryable{T}"/> 之后的异步执行入口。</summary>
/// <remarks>
/// <para>Domain 不引用 EF Core，所以 <c>GetQueryableAsync()</c> 返回的查询在领域服务里
/// 调不到 <c>ToListAsync</c>，必须经本接口。业务项目的每个列表查询、每个计数、
/// 每个存在性判断都走这条路。</para>
/// <para>用 SQLite 而不是 InMemory：本类的全部意义是"把查询交给 Provider 异步执行"，
/// InMemory 不产生 SQL，通不过它也就证明不了这件事。</para>
/// </remarks>
public sealed class QueryableAsyncExecuterTests : IDisposable
{
    private sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool Archived { get; set; }
    }

    private sealed class QueryDbContext(DbContextOptions<QueryDbContext> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly QueryDbContext _db;
    private readonly IQueryableAsyncExecuter _executer = new EfCoreQueryableAsyncExecuter();

    public QueryableAsyncExecuterTests()
    {
        _connection.Open();
        _db = new QueryDbContext(new DbContextOptionsBuilder<QueryDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.AddRange(
            new Item { Id = 1, Name = "a" },
            new Item { Id = 2, Name = "b" },
            new Item { Id = 3, Name = "c", Archived = true });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ToList_materializes_the_query()
    {
        var result = await _executer.ToListAsync(_db.Items.OrderBy(i => i.Id));

        Assert.Equal([1, 2, 3], result.Select(i => i.Id));
    }

    // 检查 SQL 含 WHERE，避免拉全表后在内存过滤。
    [Fact]
    public async Task Where_is_translated_rather_than_evaluated_in_memory()
    {
        var query = _db.Items.Where(i => !i.Archived);

        Assert.Contains("WHERE", query.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await _executer.ToListAsync(query)).Count);
    }

    [Fact]
    public async Task Count_and_long_count_agree()
    {
        var query = _db.Items.Where(i => !i.Archived);

        Assert.Equal(2, await _executer.CountAsync(query));
        Assert.Equal(2L, await _executer.LongCountAsync(query));
    }

    [Fact]
    public async Task Count_of_an_empty_result_is_zero()
    {
        var empty = _db.Items.Where(i => i.Name == "missing");

        Assert.Equal(0, await _executer.CountAsync(empty));
        Assert.Equal(0L, await _executer.LongCountAsync(empty));
    }

    [Fact]
    public async Task First_or_default_returns_the_first_match_by_the_declared_order()
    {
        var item = await _executer.FirstOrDefaultAsync(_db.Items.OrderByDescending(i => i.Id));

        Assert.Equal(3, item!.Id);
    }

    // 无匹配返回 null 而不是抛：调用方普遍据此走"不存在"分支。
    [Fact]
    public async Task First_or_default_returns_null_when_nothing_matches()
    {
        Assert.Null(await _executer.FirstOrDefaultAsync(_db.Items.Where(i => i.Name == "missing")));
    }

    [Fact]
    public async Task Single_or_default_returns_the_only_match()
    {
        var item = await _executer.SingleOrDefaultAsync(_db.Items.Where(i => i.Name == "b"));

        Assert.Equal(2, item!.Id);
    }

    [Fact]
    public async Task Single_or_default_returns_null_when_nothing_matches()
    {
        Assert.Null(await _executer.SingleOrDefaultAsync(_db.Items.Where(i => i.Name == "missing")));
    }

    // 多于一条必须抛，不能静默取第一条——"唯一"是调用方的前置断言，
    // 静默降级会让重复数据一直藏着。
    [Fact]
    public async Task Single_or_default_throws_when_more_than_one_matches()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _executer.SingleOrDefaultAsync(_db.Items.Where(i => !i.Archived)));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("missing", false)]
    public async Task Any_reports_existence(string name, bool expected)
    {
        Assert.Equal(expected, await _executer.AnyAsync(_db.Items.Where(i => i.Name == name)));
    }

    // 取消令牌必须真的传到 Provider：吞掉它会让超时与请求中止对查询完全无效。
    [Fact]
    public async Task Cancellation_is_forwarded_to_the_provider()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executer.ToListAsync(_db.Items, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executer.CountAsync(_db.Items, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executer.LongCountAsync(_db.Items, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executer.FirstOrDefaultAsync(_db.Items, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executer.SingleOrDefaultAsync(_db.Items.Where(i => i.Id == 1), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executer.AnyAsync(_db.Items, cts.Token));
    }
}
