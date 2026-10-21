using static Leistd.TestBase.Doubles.DbContextProviderFor;
using Leistd.TestBase.Doubles;
using System.Data.Common;
using Leistd.Authorization.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Leistd.Authorization.Constants;
using Leistd.Authorization.EntityFrameworkCore.Managers;
using Leistd.Authorization.EntityFrameworkCore.Stores;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Authorization.EntityFrameworkCore.Entities;
using Leistd.Authorization.Exceptions;
using Leistd.Authorization.Tests.TestDoubles;

namespace Leistd.Authorization.Tests.EntityFrameworkCore;

/// <summary>授予存储与管理器的关系型行为验证。</summary>
/// <remarks>
/// 使用 Sqlite 而非 InMemory：只有关系型 Provider 才会真正强制唯一索引、真正翻译查询表达式，
/// InMemory 全部在内存求值，会让不可翻译的查询和被违反的唯一约束静默通过。
/// </remarks>
public class EfCorePermissionGrantStoreManagerTests : IAsyncLifetime
{
    private SqliteConnection _connection = default!;
    private TestAuthorizationDbContext _db = default!;
    private EfCorePermissionGrantStore<TestAuthorizationDbContext> _store = default!;
    private EfCorePermissionGrantManager<TestAuthorizationDbContext> _manager = default!;
    private PermissionDefinitionManager _definitions = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TestAuthorizationDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _definitions = TestPermissionDefinitions.CreateManager();
        _store = new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(_db));
        _manager = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(_db), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(_db)));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Granting_a_child_completes_its_ancestors()
    {
        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersWriteBatch,
            PermissionGrantProviderNames.Role,
            "r1");

        var grants = await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1");

        Assert.Equal(
            [
                TestPermissionDefinitionProvider.Orders,
                TestPermissionDefinitionProvider.OrdersWrite,
                TestPermissionDefinitionProvider.OrdersWriteBatch
            ],
            grants.PermissionNames.OrderBy(x => x, StringComparer.Ordinal));
            }

    [Fact]
    public async Task Revoking_a_parent_cascades_to_its_descendants()
    {
        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersWriteBatch,
            PermissionGrantProviderNames.Role,
            "r1");
        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersRead,
            PermissionGrantProviderNames.Role,
            "r1");

        await _manager.RevokeAsync(
            TestPermissionDefinitionProvider.OrdersWrite,
            PermissionGrantProviderNames.Role,
            "r1");

        var grants = await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1");

        Assert.Equal(
            [TestPermissionDefinitionProvider.Orders, TestPermissionDefinitionProvider.OrdersRead],
            grants.PermissionNames.OrderBy(x => x, StringComparer.Ordinal));
    }



    [Fact]
    public async Task Replace_rejects_undefined_or_disabled_permissions()
    {
        var undefined = await Assert.ThrowsAsync<UndefinedPermissionException>(
            () => _manager.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                "r1",
                [TestPermissionDefinitionProvider.Undefined]));
        Assert.Equal([TestPermissionDefinitionProvider.Undefined], undefined.PermissionNames);

        var disabled = await Assert.ThrowsAsync<UndefinedPermissionException>(
            () => _manager.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                "r1",
                [TestPermissionDefinitionProvider.ReportsView]));
        Assert.Equal([TestPermissionDefinitionProvider.ReportsView], disabled.PermissionNames);

        Assert.Empty((await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1")).PermissionNames);
    }

    [Fact]
    public async Task Replace_bumps_version_only_when_something_changed()
    {
        var first = await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        var unchanged = await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        var second = await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersWrite]);

        Assert.Equal(1, first);
        Assert.Equal(1, unchanged);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task Replace_with_stale_version_is_rejected_instead_of_overwriting()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead],
            expectedVersion: 0);

        var exception = await Assert.ThrowsAsync<PermissionGrantConcurrencyException>(
            () => _manager.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                "r1",
                [TestPermissionDefinitionProvider.OrdersDelete],
                expectedVersion: 0));

        Assert.Equal(0, exception.ExpectedVersion);
        Assert.Equal(1, exception.ActualVersion);

        var grants = await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1");
        Assert.Contains(grants.PermissionNames, x => x == TestPermissionDefinitionProvider.OrdersRead);
        Assert.DoesNotContain(grants.PermissionNames, x => x == TestPermissionDefinitionProvider.OrdersDelete);
    }

    [Fact]
    public async Task Subject_grants_are_fetched_for_user_and_all_roles_at_once()
    {
        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersRead,
            PermissionGrantProviderNames.User,
            "u1");
        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersWrite,
            PermissionGrantProviderNames.Role,
            "r1");
        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersDelete,
            PermissionGrantProviderNames.Role,
            "other-role");

        var subject = await _store.GetGrantsForSubjectAsync("u1", ["r1", "r2"]);

        Assert.Contains(subject.UserGrants.PermissionNames, x => x == TestPermissionDefinitionProvider.OrdersRead);
        Assert.Equal(2, subject.RoleGrants.Count);

        var granted = subject.GetGrantedNames();
        Assert.Contains(TestPermissionDefinitionProvider.OrdersRead, granted);
        Assert.Contains(TestPermissionDefinitionProvider.OrdersWrite, granted);
        Assert.DoesNotContain(TestPermissionDefinitionProvider.OrdersDelete, granted);
    }

    [Fact]
    public async Task Subject_version_token_changes_with_grants_and_with_role_membership()
    {
        var before = (await _store.GetGrantsForSubjectAsync("u1", ["r1"])).VersionToken;

        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersRead,
            PermissionGrantProviderNames.Role,
            "r1");
        var afterGrant = (await _store.GetGrantsForSubjectAsync("u1", ["r1"])).VersionToken;

        var afterMembership = (await _store.GetGrantsForSubjectAsync("u1", ["r1", "r2"])).VersionToken;

        Assert.NotEqual(before, afterGrant);
        Assert.NotEqual(afterGrant, afterMembership);
    }

    [Fact]
    public async Task Unique_index_rejects_duplicate_grant_rows()
    {
        await _manager.GrantAsync(
            TestPermissionDefinitionProvider.OrdersRead,
            PermissionGrantProviderNames.Role,
            "r1");

        _db.Set<PermissionGrantRecord>().Add(new PermissionGrantRecord
        {
            PermissionName = TestPermissionDefinitionProvider.OrdersRead,
            ProviderName = PermissionGrantProviderNames.Role,
            ProviderKey = "r1"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Interleaved_writes_on_the_same_subject_lose_the_optimistic_concurrency_check()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var firstDb = new TestAuthorizationDbContext(options);
        await using var secondDb = new TestAuthorizationDbContext(options);
        var first = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(firstDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(firstDb)));
        var second = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(secondDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(secondDb)));

        // 两个上下文都把版本读进内存后才发生写入：这正是「先读后比」保护不了的窗口。
        var version = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1")).Version;
        await firstDb.Set<AuthorizationVersionRecord>().AsTracking().ToListAsync();
        await secondDb.Set<AuthorizationVersionRecord>().AsTracking().ToListAsync();

        await first.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersWrite],
            version);

        var conflict = await Assert.ThrowsAsync<PermissionGrantConcurrencyException>(
            () => second.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                "r1",
                [TestPermissionDefinitionProvider.OrdersDelete],
                version));

        Assert.Equal(PermissionGrantProviderNames.Role, conflict.ProviderName);
        // 赢家已把版本推到 2，异常必须报存储中的真实值而非落败方读到的 1。
        Assert.Equal(2, conflict.ActualVersion);

        var grants = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1")).PermissionNames;
        Assert.Contains(grants, x => x == TestPermissionDefinitionProvider.OrdersWrite);
        Assert.DoesNotContain(grants, x => x == TestPermissionDefinitionProvider.OrdersDelete);
    }

    [Fact]
    public async Task Competing_first_writes_on_the_same_subject_surface_as_a_concurrency_conflict()
    {
        const string roleKey = "brand-new-role";

        // 确定性交错：落败方读到"版本行不存在"之后、提交之前，另一方才完成首次写入。
        // 这是插入路径特有的竞争窗口——此处没有可比对的版本行，并发令牌帮不上忙，
        // 冲突只会表现为唯一索引违例。
        var interceptor = new RunOnceBeforeSaveInterceptor(async () =>
        {
            var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
                .UseSqlite(_connection)
                .Options;

            await using var winnerDb = new TestAuthorizationDbContext(options);
            var winner = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(winnerDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(winnerDb)));
            await winner.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                roleKey,
                [TestPermissionDefinitionProvider.OrdersRead]);
        });

        var loserOptions = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var loserDb = new TestAuthorizationDbContext(loserOptions);
        var loser = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(loserDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(loserDb)));

        var conflict = await Assert.ThrowsAsync<PermissionGrantConcurrencyException>(
            () => loser.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                roleKey,
                [TestPermissionDefinitionProvider.OrdersWrite]));

        Assert.Equal(roleKey, conflict.ProviderKey);
        // 实际版本取自存储，而不是本次写入前读到的 0。
        Assert.Equal(1, conflict.ActualVersion);

        var grants = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, roleKey)).PermissionNames;
        Assert.Contains(grants, x => x == TestPermissionDefinitionProvider.OrdersRead);
        Assert.DoesNotContain(grants, x => x == TestPermissionDefinitionProvider.OrdersWrite);
    }

    [Fact]
    public async Task A_losing_first_write_leaves_nothing_pending_in_the_host_unit_of_work()
    {
        const string roleKey = "loser-first-write";

        var interceptor = new RunOnceBeforeSaveInterceptor(async () =>
        {
            var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
                .UseSqlite(_connection)
                .Options;

            await using var winnerDb = new TestAuthorizationDbContext(options);
            var winner = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(winnerDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(winnerDb)));
            await winner.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                roleKey,
                [TestPermissionDefinitionProvider.OrdersRead]);
        });

        var loserOptions = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var loserDb = new TestAuthorizationDbContext(loserOptions);
        var loser = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(loserDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(loserDb)));

        await Assert.ThrowsAsync<PermissionGrantConcurrencyException>(
            () => loser.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                roleKey,
                [TestPermissionDefinitionProvider.OrdersWrite]));

        // Manager 用的是宿主的 DbContext。调用方接住冲突后继续提交自己的工作单元是完全合法的，
        // 此时落败方的授予不得搭车落库——否则库里会出现"授予写成功但版本没涨"的状态，
        // 之后所有乐观并发都会拿着一个对不上的版本号做判断。
        await loserDb.SaveChangesAsync();

        var grants = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, roleKey)).PermissionNames;
        Assert.Contains(grants, x => x == TestPermissionDefinitionProvider.OrdersRead);
        Assert.DoesNotContain(grants, x => x == TestPermissionDefinitionProvider.OrdersWrite);

        var version = await loserDb.Set<AuthorizationVersionRecord>()
            .AsNoTracking()
            .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == roleKey)
            .Select(x => x.Version)
            .SingleAsync();
        Assert.Equal(1, version);
    }

    [Fact]
    public async Task A_losing_update_leaves_nothing_pending_in_the_host_unit_of_work()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var firstDb = new TestAuthorizationDbContext(options);
        await using var secondDb = new TestAuthorizationDbContext(options);
        var first = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(firstDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(firstDb)));
        var second = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(secondDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(secondDb)));

        var version = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1")).Version;
        await firstDb.Set<AuthorizationVersionRecord>().AsTracking().ToListAsync();
        await secondDb.Set<AuthorizationVersionRecord>().AsTracking().ToListAsync();

        await first.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersWrite],
            version);

        await Assert.ThrowsAsync<PermissionGrantConcurrencyException>(
            () => second.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                "r1",
                [TestPermissionDefinitionProvider.OrdersDelete],
                version));

        // 更新路径同理：版本行已被赢家推高，落败方手里的增删与版本号都必须彻底作废。
        await secondDb.SaveChangesAsync();

        var grants = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1")).PermissionNames;
        Assert.Contains(grants, x => x == TestPermissionDefinitionProvider.OrdersWrite);
        Assert.DoesNotContain(grants, x => x == TestPermissionDefinitionProvider.OrdersDelete);

        var actual = await secondDb.Set<AuthorizationVersionRecord>()
            .AsNoTracking()
            .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == "r1")
            .Select(x => x.Version)
            .SingleAsync();
        Assert.Equal(2, actual);
    }

    [Fact]
    public async Task An_unrelated_concurrency_conflict_is_not_reported_as_a_permission_conflict()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var seedDb = new TestAuthorizationDbContext(options);
        var business = new BusinessRecord { Name = "order-1", Version = 1 };
        seedDb.Add(business);
        await seedDb.SaveChangesAsync();

        // 别人先改了这条业务记录，把它的并发令牌推走。
        await using (var winnerDb = new TestAuthorizationDbContext(options))
        {
            var winner = await winnerDb.Set<BusinessRecord>().SingleAsync(x => x.Id == business.Id);
            winner.Name = "order-1-updated";
            winner.Version += 1;
            await winnerDb.SaveChangesAsync();
        }

        await using var hostDb = new TestAuthorizationDbContext(options);
        var manager = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(Fixed(hostDb), _definitions, new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(hostDb)));

        // 宿主在同一个工作单元里既改业务实体（持有过期令牌），又改权限授予。
        var stale = await hostDb.Set<BusinessRecord>().SingleAsync(x => x.Id == business.Id);
        hostDb.Entry(stale).Property(x => x.Version).OriginalValue = 1;
        stale.Name = "order-1-conflicting";

        var version = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1")).Version;

        // 冲突来自业务实体，权限版本根本没被别人动过。若无条件把它翻译成权限冲突，
        // 界面会提示"权限已被其他管理员修改"，真正的业务并发问题就此消失。
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => manager.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                "r1",
                [TestPermissionDefinitionProvider.OrdersRead, TestPermissionDefinitionProvider.OrdersWrite],
                version));
    }

    [Fact]
    public async Task A_single_grant_does_not_silently_drop_a_concurrent_write()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .Options;

        // 交错点必须落在"读集合"与"读版本"之间：放到 SaveChanges 上是没用的，
        // 那时 EF 的并发令牌本来就会拦下，无论有没有携带 expectedVersion。
        // 真正的危险窗口是本次拿着旧集合、却读到了对方写完后的新版本。
        await using var loserDb = new TestAuthorizationDbContext(options);
        var probingStore = new WriteOnceAfterReadStore(
            new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(loserDb)),
            async () =>
            {
                await using var winnerDb = new TestAuthorizationDbContext(options);
                var winner = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(
                    Fixed(winnerDb),
                    _definitions,
                    new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(winnerDb)));

                await winner.GrantAsync(
                    TestPermissionDefinitionProvider.OrdersWrite,
                    PermissionGrantProviderNames.Role,
                    "r1");
            });

        var loser = new EfCorePermissionGrantManager<TestAuthorizationDbContext>(
            Fixed(loserDb),
            _definitions,
            probingStore);

        await loser.GrantAsync(
            TestPermissionDefinitionProvider.OrdersDelete,
            PermissionGrantProviderNames.Role,
            "r1");

        // 不带版本写回时，本次会拿旧集合配新版本，把对方刚加的 OrdersWrite 删掉且不报错。
        var grants = (await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1")).PermissionNames;
        Assert.Contains(grants, x => x == TestPermissionDefinitionProvider.OrdersRead);
        Assert.Contains(grants, x => x == TestPermissionDefinitionProvider.OrdersWrite);
        Assert.Contains(grants, x => x == TestPermissionDefinitionProvider.OrdersDelete);
    }

    [Fact]
    public async Task Removing_a_provider_clears_grants_and_version_and_is_idempotent()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead, TestPermissionDefinitionProvider.OrdersWrite]);

        var before = await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1");
        Assert.NotEmpty(before.PermissionNames);
        Assert.Equal(1, before.Version);

        var removed = await _manager.RemoveProviderAsync(PermissionGrantProviderNames.Role, "r1");
        Assert.Equal(before.PermissionNames.Count, removed);

        // 版本行必须一起消失：主体已经永久删除，留着它只会变成永久孤儿；
        // 这与"撤销到空集合"不同——那种情况要保留并递增版本。
        var after = await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1");
        Assert.Empty(after.PermissionNames);
        Assert.Equal(0, after.Version);

        Assert.Equal(0, await _manager.RemoveProviderAsync(PermissionGrantProviderNames.Role, "r1"));
    }

    [Fact]
    public async Task Revoking_to_an_empty_set_keeps_the_version()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        await _manager.ReplaceGrantsAsync(PermissionGrantProviderNames.Role, "r1", []);

        // 撤销是有人还在编辑的场景：版本必须保留并递增，否则对方的乐观并发失去参照。
        var after = await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1");
        Assert.Empty(after.PermissionNames);
        Assert.Equal(2, after.Version);
    }

    [Fact]
    public async Task A_never_settling_snapshot_fails_as_a_read_error()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);

        // 每读一次版本就把它推高一格，"版本→数据→版本"永远对不上，重试必然耗尽。
        var interceptor = new BumpVersionAfterEachReadInterceptor(_connection);

        var options = new DbContextOptionsBuilder<TestAuthorizationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new TestAuthorizationDbContext(options);
        var store = new EfCorePermissionGrantStore<TestAuthorizationDbContext>(Fixed(db));

        // 抛的必须是读取失败，而不是保存冲突：这里没有调用方提交的期望版本，
        // 复用 PermissionGrantConcurrencyException 会让宿主把它当成"旧页面撞车"报成 409。
        var failure = await Assert.ThrowsAsync<UnstableGrantSnapshotException>(
            () => store.GetGrantsAsync(PermissionGrantProviderNames.Role, "r1"));

        Assert.Contains("r1", failure.Subject);
        Assert.True(failure.Attempts > 1);
    }

    /// <summary>每次读到授权版本表之后，用另一条连接把版本推高一格。</summary>
    private sealed class BumpVersionAfterEachReadInterceptor(SqliteConnection connection) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("AuthorizationVersion", StringComparison.Ordinal))
                return result;

            await using var bump = connection.CreateCommand();
            bump.CommandText = "UPDATE \"AuthorizationVersionRecord\" SET \"Version\" = \"Version\" + 1";
            await bump.ExecuteNonQueryAsync(cancellationToken);

            return result;
        }
    }

    /// <summary>在首次返回授予快照之后执行一次给定动作，用于把并发写入夹进"读集合→读版本"之间。</summary>
    private sealed class WriteOnceAfterReadStore(IPermissionGrantStore inner, Func<Task> afterFirstRead)
        : IPermissionGrantStore
    {
        private int executed;

        public async Task<PermissionGrantSet> GetGrantsAsync(
            string providerName,
            string providerKey,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.GetGrantsAsync(providerName, providerKey, cancellationToken);

            if (Interlocked.Exchange(ref executed, 1) == 0)
            {
                await afterFirstRead();
            }

            return result;
        }

        public Task<IReadOnlyList<PermissionGrantSet>> GetGrantsAsync(
            string providerName,
            IReadOnlyCollection<string> providerKeys,
            CancellationToken cancellationToken = default)
            => inner.GetGrantsAsync(providerName, providerKeys, cancellationToken);

        public Task<SubjectPermissionGrants> GetGrantsForSubjectAsync(
            string userId,
            IReadOnlyCollection<string> roleIds,
            CancellationToken cancellationToken = default)
            => inner.GetGrantsForSubjectAsync(userId, roleIds, cancellationToken);
    }

    [Fact]
    public async Task Grants_for_many_subjects_are_fetched_in_a_constant_number_of_round_trips()
    {
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r1",
            [TestPermissionDefinitionProvider.OrdersRead]);
        await _manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            "r2",
            [TestPermissionDefinitionProvider.OrdersWriteBatch]);

        var sets = await _store.GetGrantsAsync(PermissionGrantProviderNames.Role, ["r1", "r2", "r3"]);

        Assert.Equal(3, sets.Count);
        Assert.Equal(["r1", "r2", "r3"], sets.Select(x => x.ProviderKey));
        // r2 授予了孙子级，写入归一化补齐了两级祖先。
        Assert.Equal(3, sets.Single(x => x.ProviderKey == "r2").PermissionNames.Count);
        // 从未写入过的主体返回空集合与 0 版本，而不是被跳过。
        var missing = sets.Single(x => x.ProviderKey == "r3");
        Assert.Empty(missing.PermissionNames);
        Assert.Equal(0, missing.Version);
    }

    [Theory]
    [InlineData("", "r1")]
    [InlineData(PermissionGrantProviderNames.Role, "  ")]
    public async Task Blank_subject_identifiers_are_rejected_at_the_library_boundary(
        string providerName,
        string providerKey)
    {
        // 框架公共 API 的边界守卫：空白标识落库会产生谁也看不到、谁也删不掉的孤儿授予。
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.ReplaceGrantsAsync(
            providerName,
            providerKey,
            [TestPermissionDefinitionProvider.OrdersRead]));
    }

    /// <summary>与授权无关的业务实体，带自己的并发令牌，用于验证 Manager 不会冒领别人的冲突。</summary>
    private sealed class BusinessRecord
    {
        public Guid Id { get; set; } = Guid.CreateVersion7();
        public string Name { get; set; } = string.Empty;
        public int Version { get; set; }
    }

    private sealed class TestAuthorizationDbContext(DbContextOptions<TestAuthorizationDbContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigurePermissionAuthorization();

            modelBuilder.Entity<BusinessRecord>(builder =>
            {
                builder.ToTable("BusinessRecords");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Version).IsConcurrencyToken();
            });
        }
    }
}
