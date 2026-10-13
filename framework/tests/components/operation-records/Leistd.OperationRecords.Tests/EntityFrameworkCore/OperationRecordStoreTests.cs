using Leistd.Data.Paging;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.EntityFrameworkCore.Stores;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.TestBase.Doubles;
using Leistd.UnitOfWork;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.OperationRecords.Tests.EntityFrameworkCore;

/// <summary>存储与表结构。</summary>
/// <remarks>
/// 用 SQLite 而不是 InMemory：列长度、枚举字符串转换与倒序索引只有真正建库时才产生 DDL，
/// InMemory 全内存求值会让整个 <c>OperationRecordConfiguration</c> 静默通过。
/// </remarks>
public sealed class OperationRecordStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContext _db;
    private readonly ServiceProvider _services;
    private readonly EfCoreOperationRecordStore<TestDbContext> _store;

    public OperationRecordStoreTests()
    {
        _connection.Open();
        _db = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        // 本组只关心表结构与查询：上下文固定成同一个，失败记录的独立工作单元因此落回同一张表。
        // 事务边界与跨层写入由 FailedRecordIsolationTests 在真实路由下验证。
        _services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).AddUnitOfWork().BuildServiceProvider();
        _store = new EfCoreOperationRecordStore<TestDbContext>(
            new FixedDbContextProvider<TestDbContext>(_db),
            _services.GetRequiredService<IUnitOfWorkManager>(),
            new FakeCurrentTenant(null));
    }

    // 用例按"筛选维度"表达意图；组装成存储的筛选条件与分页请求集中在这一处
    private Task<PagedResult<OperationRecordInfo>> QueryAsync(
        string? keyword = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int skip = 0,
        int take = 10,
        OperationRecordVisibilityScope scope = null!,
        IReadOnlyCollection<string>? actions = null,
        OperationRecordOutcome? outcome = null)
        => _store.GetPagedListAsync(
            new OperationRecordCriteria
            {
                Scope = scope,
                Keyword = keyword,
                StartTime = startTime,
                EndTime = endTime,
                Actions = actions,
                Outcome = outcome
            },
            new PageRequest { Offset = skip, Limit = take });

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private static OperationRecordInfo Info(
        string action = "identity.user.created",
        string targetId = "u-1",
        string? actorName = "Grace",
        string? targetName = "Ada Lovelace",
        DateTime? creationTime = null,
        Guid? id = null,
        OperationRecordOutcome outcome = OperationRecordOutcome.Succeeded,
        // 默认沿用 OperationRecordInfo 自身的安全默认（Host）：不关心可见性的用例
        // 因此都落在最严格的一档，查询时显式传 Unrestricted 照常查得到。
        OperationVisibility visibility = OperationVisibility.Host,
        string? actorId = null,
        Guid? actorTenantId = null) => new()
        {
            Id = id ?? Guid.CreateVersion7(),
            Action = action,
            TargetId = targetId,
            TargetName = targetName,
            AuthorizationBasis = "App.Users.Create",
            Outcome = outcome,
            Visibility = visibility,
            CreationTime = creationTime ?? new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            ActorId = actorId,
            ActorTenantId = actorTenantId,
            ActorName = actorName
        };

    // 表名跟着宿主的 DbSet 属性名走，组件不写死它、也不加框架前缀污染宿主库。
    [Fact]
    public void The_table_name_follows_the_host_DbSet_name()
    {
        Assert.Equal("OperationRecords", _db.Model.FindEntityType(typeof(OperationRecord))!.GetTableName());
    }

    /// <summary>结果以字符串落库。</summary>
    /// <remarks>
    /// 审计表会被人直接查，序号要对着枚举定义翻译才看得懂；枚举重排之后历史行的含义还会静默改变。
    /// </remarks>
    [Fact]
    public async Task The_outcome_is_stored_as_text()
    {
        await _store.InsertAsync(Info(outcome: OperationRecordOutcome.Failed));

        var stored = await _db.Database
            .SqlQuery<string>($"SELECT \"Outcome\" AS \"Value\" FROM \"OperationRecords\"")
            .SingleAsync();

        Assert.Equal("Failed", stored);
    }

    [Fact]
    public async Task An_inserted_record_round_trips_through_the_store()
    {
        var info = Info();

        await _store.InsertAsync(info);
        var page = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        var read = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(info.Action, read.Action);
        Assert.Equal(info.TargetId, read.TargetId);
        Assert.Equal(info.CreationTime, read.CreationTime);
        Assert.Equal(OperationRecordOutcome.Succeeded, read.Outcome);
    }

    /// <summary>同一时刻的多条记录有稳定顺序，翻页不会重复或遗漏。</summary>
    /// <remarks>
    /// <para>只按时间排序时，同一毫秒内写入的多条记录在两次查询之间可能换序，
    /// 于是翻页会重复或漏掉记录——而漏掉的那条没有任何迹象。次级键 <c>Id</c> 消除这种不确定。</para>
    /// <para><b>只断言"稳定"，不断言"更新的在前"</b>：同一时刻内的先后取决于 Provider 怎么比较
    /// Guid。PostgreSQL 的 <c>uuid</c> 按网络字节序比较，v7 的时间序成立；SQLite 把 Guid 存成 BLOB、
    /// 按 .NET 字节布局逐字节比较，而那个布局前三段是小端，时间序不成立。
    /// 分页要的是确定性，不是这一档的先后。</para>
    /// </remarks>
    [Fact]
    public async Task Records_sharing_a_timestamp_have_a_stable_order()
    {
        var sameInstant = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        foreach (var id in ids)
        {
            await _store.InsertAsync(Info(id: id, creationTime: sameInstant));
        }

        var firstPass = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);
        var secondPass = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        // 同一批数据两次查询顺序一致；且逐页取与整批取给出同一个序列——翻页因此不重不漏
        Assert.Equal(firstPass.Items.Select(x => x.Id), secondPass.Items.Select(x => x.Id));
        Assert.Equal(ids.Order(), firstPass.Items.Select(x => x.Id).Order());

        var pageOne = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 0, take: 2, scope: OperationRecordVisibilityScope.Unrestricted);
        var pageTwo = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 2, take: 2, scope: OperationRecordVisibilityScope.Unrestricted);
        Assert.Equal(
            firstPass.Items.Select(x => x.Id),
            pageOne.Items.Concat(pageTwo.Items).Select(x => x.Id));
    }

    [Fact]
    public async Task Newer_records_come_first()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await _store.InsertAsync(Info(action: "old", creationTime: older));
        await _store.InsertAsync(Info(action: "new", creationTime: newer));

        var page = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(["new", "old"], page.Items.Select(x => x.Action));
    }

    /// <summary>关键字同时匹配动作码、目标标识、目标名与操作人名。</summary>
    /// <remarks>列表展示目标时优先显示目标名，看得见的名字必须搜得到。</remarks>
    [Theory]
    [InlineData("user.created", 1)]
    [InlineData("u-1", 1)]
    [InlineData("Ada Lovelace", 1)]
    [InlineData("Grace", 1)]
    [InlineData("nothing-matches", 0)]
    public async Task The_keyword_matches_action_target_and_actor(string keyword, int expected)
    {
        await _store.InsertAsync(Info());

        var page = await QueryAsync(keyword, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(expected, page.Items.Count);
        Assert.Equal(expected, page.TotalCount);
    }

    /// <summary>按目标名命中不放宽可见性：其他读者看不到的记录，同名也搜不出来。</summary>
    [Fact]
    public async Task A_target_name_match_still_respects_visibility()
    {
        await _store.InsertAsync(Info(targetName: "Ada Lovelace", visibility: OperationVisibility.Host));
        await _store.InsertAsync(Info(targetName: "Ada Lovelace", visibility: OperationVisibility.Tenant));

        var page = await QueryAsync(keyword: "Lovelace", scope: OperationRecordVisibilityScope.ForTenantReader("reader", Guid.NewGuid()));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(OperationVisibility.Tenant, Assert.Single(page.Items).Visibility);
    }

    /// <summary>时间区间两端都是闭区间：边界那一刻的记录必须被选中。</summary>
    /// <remarks>
    /// 边界差一秒最容易被当成"那天没有操作"——而这正是审计表最不该给出的错觉，
    /// 所以这里把两端各放一条正好落在边界上的记录，连同区间外相邻的各一条一起钉住。
    /// </remarks>
    [Fact]
    public async Task The_time_range_includes_both_boundaries()
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc);

        await _store.InsertAsync(Info(action: "before", creationTime: start.AddSeconds(-1)));
        await _store.InsertAsync(Info(action: "at-start", creationTime: start));
        await _store.InsertAsync(Info(action: "at-end", creationTime: end));
        await _store.InsertAsync(Info(action: "after", creationTime: end.AddSeconds(1)));

        var page = await QueryAsync(
            keyword: null, startTime: start, endTime: end, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        // 倒序：靠后的在前
        Assert.Equal(["at-end", "at-start"], page.Items.Select(x => x.Action));
        Assert.Equal(2, page.TotalCount);
    }

    /// <summary>只给一端时，另一端不设限。</summary>
    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 3)]
    public async Task An_open_ended_range_bounds_only_the_side_given(
        bool withStart, bool withEnd, int expected)
    {
        var middle = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

        await _store.InsertAsync(Info(action: "early", creationTime: middle.AddDays(-1)));
        await _store.InsertAsync(Info(action: "middle", creationTime: middle));
        await _store.InsertAsync(Info(action: "late", creationTime: middle.AddDays(1)));

        var page = await QueryAsync(
            keyword: null,
            startTime: withStart ? middle : null,
            endTime: withEnd ? middle : null,
            skip: 0,
            take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(expected, page.TotalCount);
    }

    /// <summary>关键字与时间区间是"与"关系，不是"或"。</summary>
    [Fact]
    public async Task The_keyword_and_the_time_range_are_combined_with_and()
    {
        var inRange = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

        await _store.InsertAsync(Info(action: "user.created", creationTime: inRange));
        await _store.InsertAsync(Info(action: "user.created", creationTime: inRange.AddDays(10)));
        await _store.InsertAsync(Info(action: "role.created", creationTime: inRange));

        var page = await QueryAsync(
            keyword: "user.created",
            startTime: inRange.AddDays(-1),
            endTime: inRange.AddDays(1),
            skip: 0,
            take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(1, page.TotalCount);
    }

    /// <summary><c>Unrestricted</c> 不按可见性过滤，三层记录都返回。</summary>
    /// <remarks>
    /// 它供不代表某个读者的内部任务使用；与 <c>Host</c> 的差别在于不走可见性谓词，
    /// 此处钉住"显式不过滤"确实不过滤。
    /// </remarks>
    [Fact]
    public async Task The_unrestricted_scope_filters_nothing()
    {
        await _store.InsertAsync(Info(action: "host.only", visibility: OperationVisibility.Host));
        await _store.InsertAsync(Info(action: "tenant.visible", visibility: OperationVisibility.Tenant));
        await _store.InsertAsync(Info(action: "actor.only", visibility: OperationVisibility.Actor));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(3, page.TotalCount);
    }

    /// <summary>租户读者看不到 <c>Host</c> 层；<c>Actor</c> 层仅限本人。</summary>
    /// <remarks>
    /// 跨租户隔离不在这里——那由 <c>IMultiTenant</c> 的全局查询过滤器承担。
    /// 本段只在同一层内部再分一次"这条给不给看"。
    /// </remarks>
    [Fact]
    public async Task A_tenant_reader_sees_neither_host_records_nor_other_peoples_actor_records()
    {
        var tenantId = Guid.CreateVersion7();
        await _store.InsertAsync(Info(action: "host.only", visibility: OperationVisibility.Host));
        await _store.InsertAsync(Info(action: "tenant.visible", visibility: OperationVisibility.Tenant));
        await _store.InsertAsync(
            Info(action: "actor.mine", visibility: OperationVisibility.Actor, actorId: "me", actorTenantId: tenantId));
        await _store.InsertAsync(
            Info(action: "actor.someone-else", visibility: OperationVisibility.Actor, actorId: "other", actorTenantId: tenantId));
        // 宿主主体进入租户留下的记录：标识相同，所属租户不同，不是本人
        await _store.InsertAsync(
            Info(action: "actor.host-namesake", visibility: OperationVisibility.Actor, actorId: "me"));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10,
            scope: OperationRecordVisibilityScope.ForTenantReader("me", tenantId));

        Assert.Equal(["actor.mine", "tenant.visible"], page.Items.Select(x => x.Action).Order());
    }

    /// <summary>宿主主体在租户里读自己留下的 <c>Actor</c> 层记录：所属租户同为宿主，仍是本人。</summary>
    [Fact]
    public async Task A_host_subject_inside_a_tenant_still_sees_its_own_actor_records()
    {
        await _store.InsertAsync(
            Info(action: "actor.mine", visibility: OperationVisibility.Actor, actorId: "me"));
        await _store.InsertAsync(
            Info(action: "actor.tenant-namesake", visibility: OperationVisibility.Actor, actorId: "me", actorTenantId: Guid.CreateVersion7()));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10,
            scope: OperationRecordVisibilityScope.ForTenantReader("me", actorTenantId: null));

        Assert.Equal("actor.mine", Assert.Single(page.Items).Action);
    }

    /// <summary>宿主读者看得到全部三层，含不属于自己、乃至没有操作人的 <c>Actor</c> 层记录。</summary>
    /// <remarks>
    /// <c>Host</c> 范围不按 ActorId 过滤，即使其值为 null 也须看见 Actor 层。
    /// <c>actor.anonymous</c> 刻意构造无操作人的 Actor 记录，验证宿主读取不会被本人匹配规则排除。
    /// </remarks>
    [Fact]
    public async Task A_host_reader_sees_every_layer_including_actor_records_that_are_not_theirs()
    {
        await _store.InsertAsync(Info(action: "host.only", visibility: OperationVisibility.Host));
        await _store.InsertAsync(Info(action: "tenant.visible", visibility: OperationVisibility.Tenant));
        await _store.InsertAsync(
            Info(action: "actor.someone-else", visibility: OperationVisibility.Actor, actorId: "other"));
        await _store.InsertAsync(Info(action: "actor.anonymous", visibility: OperationVisibility.Actor));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10,
            scope: OperationRecordVisibilityScope.Host);

        Assert.Equal(
            ["actor.anonymous", "actor.someone-else", "host.only", "tenant.visible"],
            page.Items.Select(x => x.Action).Order());
    }

    /// <summary>操作人未知时，<c>Actor</c> 层一律不可见。</summary>
    /// <remarks>
    /// 匿名与机器主体没有"本人"可言。放行会让自助改密这类只关乎本人的记录漏给别人——
    /// 而这正是可见性分层要挡住的那种泄露。
    /// </remarks>
    [Fact]
    public async Task An_unknown_reader_sees_no_actor_scoped_records()
    {
        await _store.InsertAsync(
            Info(action: "actor.mine", visibility: OperationVisibility.Actor, actorId: "me"));
        await _store.InsertAsync(Info(action: "tenant.visible", visibility: OperationVisibility.Tenant));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10,
            scope: OperationRecordVisibilityScope.ForTenantReader(actorId: null, actorTenantId: null));

        Assert.Equal("tenant.visible", Assert.Single(page.Items).Action);
    }

    /// <summary>可见范围缺失时拒绝查询，而不是按"不过滤"执行。</summary>
    /// <remarks>
    /// 可见性是安全边界：可空引用只是编译期提示，反射、序列化或关掉警告的调用方仍可能传进
    /// <see langword="null"/>。此时若退化成不过滤，租户管理员就能看到别人的本人级记录。
    /// </remarks>
    [Fact]
    public async Task A_missing_scope_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: null!));
    }

    /// <summary>不传 <c>actions</c> 与 <c>outcome</c> 时不按它们过滤。</summary>
    /// <remarks>
    /// 这两个是筛选条件，缺省即"不限"；与可见范围不同，它们不是安全边界，不需要调用方显式表态。
    /// </remarks>
    [Fact]
    public async Task Omitting_the_filters_matches_everything()
    {
        await _store.InsertAsync(Info(action: "user.created"));
        await _store.InsertAsync(Info(action: "role.deleted", outcome: OperationRecordOutcome.Failed));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(2, page.TotalCount);
    }

    /// <summary>动作码集合：命中任一即匹配。</summary>
    /// <remarks>
    /// 传集合而不是单值，是因为"按类别筛"由调用方把类别展开成动作码后再传进来——
    /// 存储层不认识类别（它定义在 <c>IOperationActionDefinition</c> 上，而记录里只有动作码）。
    /// </remarks>
    [Fact]
    public async Task The_action_filter_matches_any_of_the_given_codes()
    {
        await _store.InsertAsync(Info(action: "user.created"));
        await _store.InsertAsync(Info(action: "role.deleted"));
        await _store.InsertAsync(Info(action: "tenant.created"));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted,
            actions: ["user.created", "tenant.created"]);

        Assert.Equal(["tenant.created", "user.created"], page.Items.Select(x => x.Action).Order());
        Assert.Equal(2, page.TotalCount);
    }

    /// <summary>空的动作码集合视为不过滤，而不是"一条都不匹配"。</summary>
    /// <remarks>
    /// 界面上"未选择任何动作"与"选了但选空了"是同一种意图——都表示不按动作筛。
    /// 若把空集合当成 <c>IN ()</c>，用户清空筛选后会看到空列表，而那看起来像"没有记录"。
    /// </remarks>
    [Fact]
    public async Task An_empty_action_filter_is_treated_as_no_filter()
    {
        await _store.InsertAsync(Info(action: "user.created"));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted,
            actions: []);

        Assert.Equal(1, page.TotalCount);
    }

    /// <summary>结果筛选：枚举列存的是字符串，比较必须真的下推到 SQL。</summary>
    /// <remarks>
    /// <c>Outcome</c> 配了 <c>HasConversion&lt;string&gt;()</c>。若比较被客户端求值，
    /// 表面结果仍然对，但整表会被拉进内存、且 <c>TotalCount</c> 按过滤前算——
    /// 所以这里同时断言条数与总数。
    /// </remarks>
    [Fact]
    public async Task The_outcome_filter_is_pushed_down_to_the_database()
    {
        await _store.InsertAsync(Info(action: "a", outcome: OperationRecordOutcome.Succeeded));
        await _store.InsertAsync(Info(action: "b", outcome: OperationRecordOutcome.Failed));
        await _store.InsertAsync(Info(action: "c", outcome: OperationRecordOutcome.Failed));

        var page = await QueryAsync(
            keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted,
            outcome: OperationRecordOutcome.Failed);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, item => Assert.Equal(OperationRecordOutcome.Failed, item.Outcome));
    }

    /// <summary>多个筛选条件之间是「与」，不是「或」。</summary>
    [Fact]
    public async Task The_filters_are_combined_with_and()
    {
        var inRange = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.InsertAsync(
            Info(action: "user.created", outcome: OperationRecordOutcome.Failed, creationTime: inRange));
        // 动作对但结果不对
        await _store.InsertAsync(
            Info(action: "user.created", outcome: OperationRecordOutcome.Succeeded, creationTime: inRange));
        // 动作与结果都对，但落在时间区间外
        await _store.InsertAsync(
            Info(action: "user.created", outcome: OperationRecordOutcome.Failed, creationTime: inRange.AddDays(30)));

        var page = await QueryAsync(
            keyword: null,
            startTime: inRange.AddDays(-1),
            endTime: inRange.AddDays(1),
            skip: 0,
            take: 10, scope: OperationRecordVisibilityScope.Unrestricted,
            actions: ["user.created"],
            outcome: OperationRecordOutcome.Failed);

        Assert.Equal(1, page.TotalCount);
    }

    /// <summary>总数是过滤后的总数，不是当页条数——否则翻页器会少给页。</summary>
    [Fact]
    public async Task The_total_count_reflects_the_filter_not_the_page()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.InsertAsync(Info(targetId: $"u-{i}"));
        }

        var page = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 0, take: 2, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.TotalCount);
    }

    /// <summary>超长字段就地截断，不让一次已经成功的业务操作因为审计字段过长而失败。</summary>
    [Fact]
    public async Task An_overlong_field_is_truncated_rather_than_rejected()
    {
        await _store.InsertAsync(Info(action: new string('x', OperationRecordInfo.MaxActionLength + 50)));

        var page = await QueryAsync(keyword: null, startTime: null, endTime: null, skip: 0, take: 10, scope: OperationRecordVisibilityScope.Unrestricted);

        Assert.Equal(OperationRecordInfo.MaxActionLength, Assert.Single(page.Items).Action.Length);
    }

    /// <summary>读与归档两条访问路径各有索引。</summary>
    /// <remarks>
    /// <para>读是"某租户的最近若干条"，按 (租户, 时间)；归档是整库按时间扫最旧的
    /// （<c>IgnoreQueryFilters</c>，不带租户），只能靠单列时间索引。后者不补，归档每一批
    /// 都要全表扫加排序，而这张表只涨不消。</para>
    /// <para>只断言列组合，与 <c>NotificationRecordSchemaTests</c> 同口径——排序方向不进运行期模型
    /// （读它会抛 <c>The requested configuration is not stored in the read-optimized model</c>），
    /// 要验证降序得比对迁移产出的 DDL，那属于宿主项目的迁移测试。</para>
    /// </remarks>
    [Fact]
    public void Both_the_read_and_the_archive_paths_have_an_index()
    {
        var indexes = _db.Model.FindEntityType(typeof(OperationRecord))!
            .GetIndexes()
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToArray();

        Assert.Contains(
            $"{nameof(OperationRecord.TenantId)},{nameof(OperationRecord.CreationTime)}",
            indexes);
        Assert.Contains(nameof(OperationRecord.CreationTime), indexes);
    }
}
