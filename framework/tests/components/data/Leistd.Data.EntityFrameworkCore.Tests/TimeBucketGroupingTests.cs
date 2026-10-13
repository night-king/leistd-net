using System.Text.RegularExpressions;
using Leistd.Data.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.Data.EntityFrameworkCore.Tests;

/// <summary>按时间桶边界分组：半开区间、不等长桶，以及翻译成 SQL 后的形状。</summary>
/// <remarks>用 SQLite 而不是 InMemory：要证明的是分组与聚合由数据库完成、嵌套层数与参数化符合预期，InMemory 不产生 SQL。</remarks>
public sealed partial class TimeBucketGroupingTests : IDisposable
{
    private static readonly DateTime Day1 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Event
    {
        public int Id { get; set; }
        public DateTime Time { get; set; }
    }

    private sealed class EventDbContext(DbContextOptions<EventDbContext> options) : DbContext(options)
    {
        public DbSet<Event> Events => Set<Event>();
    }

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly EventDbContext _db;

    public TimeBucketGroupingTests()
    {
        _connection.Open();
        _db = new EventDbContext(new DbContextOptionsBuilder<EventDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// 每个桶是半开区间：内部边界上的元素只归后一个桶；首边界计入、尾边界不计入，范围之外的不出现；
    /// 桶可以不等长（夏令时当天的日桶是 23 或 25 小时）。
    /// </summary>
    [Fact]
    public async Task Elements_fall_into_half_open_buckets_of_any_length()
    {
        var shortDayEnd = Day1.AddHours(23);
        var longDayEnd = shortDayEnd.AddHours(25);
        await SeedAsync(
            Day1.AddTicks(-1), Day1, shortDayEnd.AddTicks(-1), shortDayEnd, longDayEnd.AddTicks(-1), longDayEnd);

        var counts = await CountAsync(Day1, shortDayEnd, longDayEnd);

        Assert.Equal([2, 2], counts);
    }

    /// <summary>一年的日桶（366 个桶、367 个边界）翻译成一条语句：桶号二分嵌套不超过 9 层，边界作为参数传入。</summary>
    [Fact]
    public async Task A_year_of_daily_buckets_is_one_statement_with_shallow_parameterized_cases()
    {
        var edges = Enumerable.Range(0, 367).Select(day => Day1.AddDays(day)).ToArray();
        await SeedAsync(edges[0], edges[183].AddHours(12), edges[365].AddHours(23), edges[366]);

        var query = _db.Events.GroupByTimeBucket(x => x.Time, edges)
            .Select(group => new { Bucket = group.Key, Count = group.Count() });
        var sql = query.ToQueryString();
        var rows = await query.ToListAsync();

        Assert.Equal([(0, 1), (183, 1), (365, 1)], rows.OrderBy(row => row.Bucket).Select(row => (row.Bucket, row.Count)));
        Assert.InRange(MaxCaseDepth(sql), 1, 9);
        Assert.DoesNotMatch(LiteralDate(), sql.Split("SELECT", 2)[1]);
    }

    /// <summary>边界不足两个、不严格递增（含颠倒）都是调用方的错，在组查询时就抛出。</summary>
    [Fact]
    public void Too_few_or_non_increasing_edges_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _db.Events.GroupByTimeBucket(x => x.Time, [Day1]));
        Assert.Throws<ArgumentException>(() => _db.Events.GroupByTimeBucket(x => x.Time, [Day1, Day1]));
        Assert.Throws<ArgumentException>(() => _db.Events.GroupByTimeBucket(x => x.Time, [Day1.AddDays(1), Day1]));
    }

    private async Task<int[]> CountAsync(params DateTime[] edges)
    {
        var rows = await _db.Events.GroupByTimeBucket(x => x.Time, edges)
            .Select(group => new { Bucket = group.Key, Count = group.Count() })
            .ToListAsync();
        var counts = new int[edges.Length - 1];
        rows.ForEach(row => counts[row.Bucket] = row.Count);
        return counts;
    }

    private async Task SeedAsync(params DateTime[] times)
    {
        _db.Events.AddRange(times.Select(time => new Event { Time = time }));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static int MaxCaseDepth(string sql)
    {
        int depth = 0, max = 0;
        foreach (Match token in CaseOrEnd().Matches(sql))
        {
            depth += token.Value == "CASE" ? 1 : -1;
            max = Math.Max(max, depth);
        }

        return max;
    }

    [GeneratedRegex(@"\b(CASE|END)\b")]
    private static partial Regex CaseOrEnd();

    // 字面量形式的日期（'2026-03-01 ...'）出现在语句里说明边界没有参数化
    [GeneratedRegex(@"'\d{4}-\d{2}-\d{2}")]
    private static partial Regex LiteralDate();
}
