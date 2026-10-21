using Leistd.Data;
using Leistd.Data.Filters;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.EntityFrameworkCore.Stores;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
using Leistd.TestBase.Doubles;
using Leistd.UnitOfWork;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.OperationRecords.Tests.EntityFrameworkCore;

/// <summary>计数覆盖原表与归档表，租户、可见性与半开区间在两张表上口径一致。</summary>
/// <remarks>
/// <para>保留期把旧记录搬进归档表后，只数原表的统计会悄悄变小，回看上一区间时最明显；
/// 归档表不实现 <c>IMultiTenant</c>，漏了显式租户条件就会数进同库其他租户的记录。两样都不报错。</para>
/// <para>上下文给原表挂了与 DDD 基座同形的租户过滤器（读当前租户与 <see cref="IDataFilter"/>），
/// 原表与归档表的租户口径因此可以对照断言。</para>
/// </remarks>
public sealed class OperationRecordCountTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime Day1 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = Day1.AddDays(1);
    private static readonly DateTime Day3 = Day1.AddDays(2);

    private const string LoginSucceeded = "auth.login.succeeded";
    private const string LoginFailed = "auth.login.failed";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly IDataFilter _dataFilter;
    private readonly List<DbContext> _contexts = [];

    public OperationRecordCountTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddUnitOfWork()
            .AddDataFilters()
            .BuildServiceProvider();
        _dataFilter = _services.GetRequiredService<IDataFilter>();

        using var db = Context(null);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _contexts.ForEach(context => context.Dispose());
        _services.Dispose();
        _connection.Dispose();
    }

    /// <summary>按桶计数把归档表里的记录一并算上，同一桶两张表各有记录时合并计数。</summary>
    [Fact]
    public async Task Counts_by_bucket_include_archived_records()
    {
        await SeedAsync(
            Live(LoginSucceeded, "u-1", Day2.AddHours(1)),
            Live(LoginSucceeded, "u-2", Day3.AddHours(1)),
            Archived(LoginSucceeded, "u-1", Day1.AddHours(1)),
            Archived(LoginSucceeded, "u-3", Day2.AddHours(2)));

        var counts = await CountAsync(Store(null), Day1, Day2, Day3, Day3.AddDays(1));

        Assert.Equal([1, 2, 1], counts);
    }

    /// <summary>
    /// 区间是半开的：起点上的记录计入，终点上的不计入；没有记录的桶为 0。
    /// 相邻区间首尾相接时，边界上的记录只归后一个区间。
    /// </summary>
    [Fact]
    public async Task The_interval_includes_its_start_and_excludes_its_end()
    {
        await SeedAsync(
            Live(LoginSucceeded, "u-1", Day1.AddTicks(-10)),
            Archived(LoginSucceeded, "u-2", Day1),
            Live(LoginSucceeded, "u-3", Day3.AddTicks(-10)),
            Archived(LoginSucceeded, "u-4", Day3));

        var store = Store(null);
        Assert.Equal([1, 1], await CountAsync(store, Day1, Day2, Day3));
        Assert.Equal(2, await DistinctTargetsAsync(store, Day1, Day3));
        Assert.Equal([0], await CountAsync(store, Day2, Day2.AddHours(1)));
    }

    /// <summary>不同目标数跨两张表去重：区间被归档水位切开时，同一个人前后两次登录仍只算一人；没有目标的记录不算。</summary>
    [Fact]
    public async Task Distinct_targets_are_deduplicated_across_live_and_archived_records()
    {
        await SeedAsync(
            Archived(LoginSucceeded, "u-1", Day1.AddHours(1)),
            Live(LoginSucceeded, "u-1", Day2.AddHours(1)),
            Live(LoginSucceeded, "u-2", Day2.AddHours(2)),
            Archived(LoginSucceeded, "u-3", Day1.AddHours(2)),
            Live(LoginSucceeded, OperationTarget.NoTargetId, Day2.AddHours(3)));

        Assert.Equal(3, await DistinctTargetsAsync(Store(null), Day1, Day3));
    }

    /// <summary>动作筛选在两张表上同样生效，命中任一动作即计入。</summary>
    [Fact]
    public async Task Only_the_given_actions_are_counted()
    {
        await SeedAsync(
            Live(LoginSucceeded, "u-1", Day1.AddHours(1)),
            Archived(LoginFailed, "u-2", Day1.AddHours(2)),
            Archived("identity.user.created", "u-3", Day1.AddHours(3)));

        var store = Store(null);

        Assert.Equal([1], await store.CountByBucketsAsync(OperationRecordVisibilityScope.Unrestricted, [Day1, Day2], [LoginFailed]));
        Assert.Equal([2], await store.CountByBucketsAsync(
            OperationRecordVisibilityScope.Unrestricted, [Day1, Day2], [LoginSucceeded, LoginFailed]));
        Assert.Equal([3], await CountAsync(store, Day1, Day2));
    }

    /// <summary>
    /// 计数只看当前租户层：同库其他租户与宿主的记录，无论在原表还是归档表都不计入；宿主上下文只数宿主层。
    /// </summary>
    [Theory]
    [InlineData(null, 2)]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000001", 3)]
    public async Task Counts_stay_within_the_current_tenant_layer(string? tenant, int expected)
    {
        await SeedAsync(
            Live(LoginSucceeded, "host-1", Day1.AddHours(1)),
            Archived(LoginSucceeded, "host-2", Day1.AddHours(1)),
            Live(LoginSucceeded, "a-1", Day1.AddHours(1), TenantA),
            Archived(LoginSucceeded, "a-2", Day1.AddHours(1), TenantA),
            Archived(LoginSucceeded, "a-3", Day1.AddHours(2), TenantA),
            Live(LoginSucceeded, "b-1", Day1.AddHours(1), TenantB),
            Archived(LoginSucceeded, "b-2", Day1.AddHours(1), TenantB));

        var store = Store(tenant is null ? null : Guid.Parse(tenant));

        Assert.Equal(expected, await DistinctTargetsAsync(store, Day1, Day2));
        Assert.Equal([expected], await CountAsync(store, Day1, Day2));
    }

    /// <summary>显式关闭租户过滤时，归档表与原表一起放开到全部租户，两张表不出现两种口径。</summary>
    [Fact]
    public async Task Disabling_the_tenant_filter_opens_the_archive_as_well()
    {
        await SeedAsync(
            Live(LoginSucceeded, "a-1", Day1.AddHours(1), TenantA),
            Archived(LoginSucceeded, "a-2", Day1.AddHours(1), TenantA),
            Live(LoginSucceeded, "b-1", Day1.AddHours(1), TenantB),
            Archived(LoginSucceeded, "b-2", Day1.AddHours(1), TenantB),
            Archived(LoginSucceeded, "host-1", Day1.AddHours(1)));

        using (_dataFilter.Disable<IMultiTenant>())
        {
            Assert.Equal(5, await DistinctTargetsAsync(Store(TenantA), Day1, Day2));
        }
    }

    /// <summary>
    /// 可见范围对归档记录同样生效：租户读者数不到宿主层的记录，<c>Actor</c> 层只数本人的；
    /// 宿主与 <see cref="OperationRecordVisibilityScope.Unrestricted"/> 都数得到全部。
    /// </summary>
    [Fact]
    public async Task The_visibility_scope_applies_to_archived_records()
    {
        await SeedAsync(
            Archived(LoginFailed, "tenant-visible", Day1.AddHours(1), TenantA, OperationVisibility.Tenant),
            Archived(LoginFailed, "host-only", Day1.AddHours(1), TenantA, OperationVisibility.Host),
            Archived(LoginSucceeded, "me", Day1.AddHours(1), TenantA, OperationVisibility.Actor, actorId: "me", actorTenantId: TenantA),
            Archived(LoginSucceeded, "someone-else", Day1.AddHours(1), TenantA, OperationVisibility.Actor, actorId: "someone-else", actorTenantId: TenantA),
            Live(LoginSucceeded, "someone-live", Day1.AddHours(2), TenantA, OperationVisibility.Actor, actorId: "someone-live", actorTenantId: TenantA));

        var store = Store(TenantA);

        Assert.Equal(2, await DistinctTargetsAsync(store, Day1, Day2, OperationRecordVisibilityScope.ForTenantReader("me", TenantA)));
        Assert.Equal(5, await DistinctTargetsAsync(store, Day1, Day2, OperationRecordVisibilityScope.Host));
        Assert.Equal(5, await DistinctTargetsAsync(store, Day1, Day2, OperationRecordVisibilityScope.Unrestricted));
    }

    /// <summary>
    /// 每个桶各自去重：同一个人跨两个桶各算一次，同一桶里的多次只算一次，跨原表与归档表也按同一人算；
    /// 没有目标的记录不计入。
    /// </summary>
    [Fact]
    public async Task Distinct_targets_are_deduplicated_within_each_bucket()
    {
        await SeedAsync(
            Archived(LoginSucceeded, "u-1", Day1.AddHours(1)),
            Live(LoginSucceeded, "u-1", Day1.AddHours(5)),
            Live(LoginSucceeded, "u-2", Day1.AddHours(6)),
            Live(LoginSucceeded, "u-1", Day2.AddHours(1)),
            Live(LoginSucceeded, OperationTarget.NoTargetId, Day2.AddHours(2)));

        var counts = await Store(null).CountByBucketsAsync(
            OperationRecordVisibilityScope.Unrestricted, [Day1, Day2, Day3], distinctTargets: true);

        Assert.Equal([2, 1], counts);
    }

    /// <summary>
    /// 桶可以不等长（按时区切自然日时，夏令时当天是 23 或 25 小时）；恰好落在内部边界上的记录只归后一个桶。
    /// </summary>
    [Fact]
    public async Task Buckets_may_differ_in_length_and_an_inner_edge_belongs_to_the_later_bucket()
    {
        var shortDayEnd = Day1.AddHours(23);
        var longDayEnd = shortDayEnd.AddHours(25);
        await SeedAsync(
            Live(LoginSucceeded, "u-1", shortDayEnd.AddTicks(-1)),
            Archived(LoginSucceeded, "u-2", shortDayEnd),
            Live(LoginSucceeded, "u-3", longDayEnd.AddTicks(-1)));

        Assert.Equal([1, 2], await CountAsync(Store(null), Day1, shortDayEnd, longDayEnd));
    }

    /// <summary>一年的日桶（366 个桶、367 个边界）一次算完，每条记录落进它所在的那一天。</summary>
    [Fact]
    public async Task A_year_of_daily_buckets_is_counted_in_one_query()
    {
        var edges = Enumerable.Range(0, 367).Select(day => Day1.AddDays(day)).ToArray();
        await SeedAsync(
            Live(LoginSucceeded, "u-1", edges[0]),
            Live(LoginSucceeded, "u-2", edges[1].AddTicks(-1)),
            Archived(LoginSucceeded, "u-3", edges[183].AddHours(12)),
            Live(LoginSucceeded, "u-4", edges[365].AddHours(23)),
            Live(LoginSucceeded, "u-5", edges[366]));

        var counts = await CountAsync(Store(null), edges);

        var expected = new int[366];
        expected[0] = 2;
        expected[183] = 1;
        expected[365] = 1;
        Assert.Equal(expected, counts);
    }

    /// <summary>边界不足两个、不严格递增（含首尾颠倒）都是调用方的错，抛出而不是静默给 0；缺可见范围同样拒绝。</summary>
    [Fact]
    public async Task Invalid_bucket_edges_or_a_missing_scope_are_rejected()
    {
        var store = Store(null);

        await Assert.ThrowsAsync<ArgumentException>(() => CountAsync(store, Day1));
        await Assert.ThrowsAsync<ArgumentException>(() => CountAsync(store, Day1, Day2, Day2));
        await Assert.ThrowsAsync<ArgumentException>(() => CountAsync(store, Day2, Day1));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CountByBucketsAsync(null!, [Day1, Day2]));
    }

    private static Task<IReadOnlyList<int>> CountAsync(IOperationRecordReader reader, params DateTime[] edges)
        => reader.CountByBucketsAsync(OperationRecordVisibilityScope.Unrestricted, edges);

    // 只给首尾两个边界即整段区间
    private static async Task<int> DistinctTargetsAsync(
        IOperationRecordReader reader,
        DateTime start,
        DateTime end,
        OperationRecordVisibilityScope? scope = null)
        => (await reader.CountByBucketsAsync(
            scope ?? OperationRecordVisibilityScope.Unrestricted, [start, end], distinctTargets: true))[0];

    private EfCoreOperationRecordStore<TenantFilteredDbContext> Store(Guid? tenantId)
    {
        var context = Context(tenantId);
        _contexts.Add(context);
        return new EfCoreOperationRecordStore<TenantFilteredDbContext>(
            new FixedDbContextProvider<TenantFilteredDbContext>(context),
            _services.GetRequiredService<IUnitOfWorkManager>(),
            new FakeCurrentTenant(tenantId),
            _dataFilter);
    }

    private TenantFilteredDbContext Context(Guid? tenantId) => new(
        new DbContextOptionsBuilder<TenantFilteredDbContext>().UseSqlite(_connection).Options,
        new FakeCurrentTenant(tenantId),
        _dataFilter);

    private async Task SeedAsync(params object[] rows)
    {
        await using var db = Context(null);
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static OperationRecord Live(
        string action,
        string targetId,
        DateTime time,
        Guid? tenantId = null,
        OperationVisibility visibility = OperationVisibility.Tenant,
        string? actorId = null,
        Guid? actorTenantId = null) => new()
        {
            TenantId = tenantId,
            Action = action,
            TargetId = targetId,
            AuthorizationBasis = "CredentialsPresented",
            Outcome = OperationRecordOutcome.Succeeded,
            Visibility = visibility,
            CreationTime = time,
            ActorId = actorId,
            ActorTenantId = actorTenantId
        };

    private static OperationRecordArchive Archived(
        string action,
        string targetId,
        DateTime time,
        Guid? tenantId = null,
        OperationVisibility visibility = OperationVisibility.Tenant,
        string? actorId = null,
        Guid? actorTenantId = null)
        => OperationRecordArchive.From(Live(action, targetId, time, tenantId, visibility, actorId, actorTenantId), time.AddDays(30));

    /// <summary>原表挂上与 DDD 基座同形的租户过滤器：按当前租户过滤，<see cref="IDataFilter"/> 关闭时放开。</summary>
    private sealed class TenantFilteredDbContext(
        DbContextOptions<TenantFilteredDbContext> options,
        ICurrentTenant currentTenant,
        IDataFilter dataFilter) : DbContext(options)
    {
        private Guid? CurrentTenantId => currentTenant.Id;

        private bool IsMultiTenantFilterEnabled => dataFilter.IsEnabled<IMultiTenant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureOperationRecords();
            modelBuilder.Entity<OperationRecord>()
                .HasQueryFilter(x => !IsMultiTenantFilterEnabled || x.TenantId == CurrentTenantId);
        }
    }
}
