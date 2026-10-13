using Leistd.Data.Paging;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;
using Leistd.TestBase.Doubles;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Leistd.MultiTenancy.EntityFrameworkCore.Managers;
using Leistd.MultiTenancy.EntityFrameworkCore.Stores;
using Leistd.Timing;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.Stores;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

/// <summary>租户存储与管理器的关系型行为验证（Sqlite，见 csproj 内注释）。</summary>
public class TenantStoreManagerTests : IAsyncLifetime
{
    private SqliteConnection _connection = default!;
    private TestTenantDbContext _db = default!;
    private EfCoreTenantStore<TestTenantDbContext> _store = default!;
    private EfCoreTenantManager<TestTenantDbContext> _manager = default!;

    internal class TestTenantDbContext(DbContextOptions<TestTenantDbContext> options) : DbContext(options)
    {
        /// <summary>测试用的显示名长度上限，用来制造与名称无关的约束失败。</summary>
        internal const int DisplayNameCheckLimit = 32;

        /// <summary>与租户无关的业务实体：验证管理器失败时不连带丢弃调用方的待提交变更。</summary>
        public DbSet<TestNote> Notes => Set<TestNote>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureMultiTenancy();

            // SQLite 不强制 VARCHAR 长度，需显式 CHECK 才能触发非名称冲突的写入失败
            modelBuilder.Entity<TestNote>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Text).HasMaxLength(64);
            });

            modelBuilder.Entity<TenantRecord>()
                .ToTable(t => t.HasCheckConstraint(
                    "CK_Test_TenantRecord_DisplayName",
                    $"\"{nameof(TenantRecord.DisplayName)}\" IS NULL OR length(\"{nameof(TenantRecord.DisplayName)}\") <= {DisplayNameCheckLimit}"));
        }
    }

    /// <summary>宿主工作单元里的普通业务实体。</summary>
    internal class TestNote
    {
        public Guid Id { get; set; } = Guid.CreateVersion7();

        public string Text { get; set; } = string.Empty;
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TestTenantDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TestTenantDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        var provider = new FixedDbContextProvider<TestTenantDbContext>(_db);
        _store = new EfCoreTenantStore<TestTenantDbContext>(provider);
        _manager = new EfCoreTenantManager<TestTenantDbContext>(
            provider, new UpperInvariantTenantNormalizer(), new UtcClockProvider());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Create_normalizes_name_and_store_finds_by_id_and_name()
    {
        var record = await _manager.CreateAsync("Acme", "Acme Inc.", isActive: true);

        Assert.Equal("ACME", record.NormalizedName);
        Assert.NotEqual(default, record.CreationTime);

        var byId = await _store.FindAsync(record.Id);
        Assert.NotNull(byId);
        Assert.Equal("Acme", byId.Name);
        Assert.True(byId.IsActive);
        Assert.Equal(record.CreationTime, byId.CreationTime);

        var byName = await _store.FindByNameAsync("ACME");
        Assert.NotNull(byName);
        Assert.Equal(record.Id, byName.Id);
    }

    /// <summary>停用态创建：调用方要在创建后继续初始化租户数据时，租户不能一出生就对外可用。</summary>
    [Fact]
    public async Task Tenant_can_be_created_inactive_and_activated_afterwards()
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: false);

        Assert.False(record.IsActive);
        Assert.False((await _store.FindAsync(record.Id))!.IsActive);
        Assert.False((await _store.FindByNameAsync("ACME"))!.IsActive);

        await _manager.SetActiveAsync(record.Id, true);
        Assert.True((await _store.FindAsync(record.Id))!.IsActive);
    }

    [Fact]
    public async Task Duplicate_name_is_rejected_case_insensitively()
    {
        await _manager.CreateAsync("Acme", null, isActive: true);

        await Assert.ThrowsAsync<DuplicateTenantNameException>(() => _manager.CreateAsync("ACME", null, isActive: true));
        await Assert.ThrowsAsync<DuplicateTenantNameException>(() => _manager.CreateAsync("acme", null, isActive: true));
    }

    [Fact]
    public async Task Description_round_trips_through_create_update_and_both_read_paths()
    {
        var record = await _manager.CreateAsync(
            "Acme", "Acme Inc.", isActive: true, description: "华东区自营资金账户");

        Assert.Equal("华东区自营资金账户", record.Description);

        // 两条读路径都要带上：只在其中一条映射，界面会随入口不同显示成有或没有
        var byId = await _store.FindAsync(record.Id);
        Assert.NotNull(byId);
        Assert.Equal("华东区自营资金账户", byId.Description);

        var byName = await _store.FindByNameAsync("ACME");
        Assert.NotNull(byName);
        Assert.Equal("华东区自营资金账户", byName.Description);

        var updated = await _manager.UpdateAsync(record.Id, "Acme", "Acme Inc.", description: "改过的描述");
        Assert.Equal("改过的描述", updated.Description);
    }

    [Fact]
    public async Task Updating_with_a_null_description_clears_it()
    {
        // 清空必须真的落库：把"未传"当成"不改"会让界面上清不掉描述。
        var record = await _manager.CreateAsync("Acme", null, isActive: true, description: "初始描述");

        await _manager.UpdateAsync(record.Id, "Acme", null, description: null);

        var reloaded = await _store.FindAsync(record.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.Description);
    }

    [Fact]
    public async Task Rename_takes_effect_on_both_old_and_new_name_lookups()
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: true);

        // 先按两条读路径各读一次：有缓存的实现会在这里落下陈旧条目
        Assert.NotNull(await _store.FindByNameAsync("ACME"));
        Assert.NotNull(await _store.FindAsync(record.Id));

        await _manager.UpdateAsync(record.Id, "Contoso", null);

        Assert.Null(await _store.FindByNameAsync("ACME"));
        var byNewName = await _store.FindByNameAsync("CONTOSO");
        Assert.NotNull(byNewName);
        Assert.Equal("Contoso", byNewName.Name);

        var byId = await _store.FindAsync(record.Id);
        Assert.NotNull(byId);
        Assert.Equal("Contoso", byId.Name);
    }

    /// <summary>停用在提交那一刻即生效，不存在"已提交但存储仍放行"的窗口。</summary>
    /// <remarks>
    /// 存储返回值里带 IsActive，中间件据此放行或 403——它是访问控制状态。
    /// 若这里挂分布式缓存，停用的生效就依赖尽力而为的失效：缓存不可用时
    /// 陈旧条目继续放行已停用租户，删除之后连重试失效都做不到（租户已软删、按 Id 找不到）。
    /// 存储直接读库，撤销时序由构造保证。
    /// </remarks>
    [Fact]
    public async Task Deactivation_takes_effect_immediately_on_commit()
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: true);
        Assert.True((await _store.FindAsync(record.Id))!.IsActive);

        await _manager.SetActiveAsync(record.Id, false);

        Assert.False((await _store.FindAsync(record.Id))!.IsActive);
        Assert.False((await _store.FindByNameAsync("ACME"))!.IsActive);
    }

    /// <summary>绕过管理器直接改库也立即可见：存储没有自己的状态可陈旧。</summary>
    /// <remarks>
    /// 有缓存的实现在这里会读到旧值，于是"写入必须经 ITenantManager"成了正确性前提；
    /// 直接读库把它降级为一条约定（管理器负责归一化与唯一校验），
    /// 而运维直接改库导致租户继续被放行这种事不会再发生。
    /// </remarks>
    [Fact]
    public async Task Direct_database_writes_are_visible_to_the_store_immediately()
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: true);
        Assert.True((await _store.FindAsync(record.Id))!.IsActive);

        await _db.Set<TenantRecord>()
            .Where(t => t.Id == record.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false));

        Assert.False((await _store.FindAsync(record.Id))!.IsActive);
    }

    [Fact]
    public async Task Delete_is_soft_and_hides_tenant_from_store()
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: true);
        Assert.NotNull(await _store.FindAsync(record.Id));

        await _manager.DeleteAsync(record.Id);

        Assert.Null(await _store.FindAsync(record.Id));
        Assert.Null(await _store.FindByNameAsync("ACME"));

        // 软删除：行仍在库中，业务数据可追溯
        var raw = await _db.Set<TenantRecord>().IgnoreQueryFilters()
            .SingleAsync(t => t.Id == record.Id);
        Assert.True(raw.IsDeleted);
        Assert.NotNull(raw.DeletionTime);
    }

    [Fact]
    public async Task Same_name_can_be_recreated_after_delete()
    {
        var first = await _manager.CreateAsync("Acme", null, isActive: true);
        await _manager.DeleteAsync(first.Id);

        var second = await _manager.CreateAsync("Acme", null, isActive: true);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, (await _store.FindByNameAsync("ACME"))!.Id);
    }

    /// <summary>租户名按单个 DNS 标签校验：按子域名解析租户时名字就是主机名的一段，不合法的名字建得出来却永远访问不到。</summary>
    [Theory]
    [InlineData("a")]
    [InlineData("Acme")]
    [InlineData("acme-2")]
    [InlineData("0-9")]
    public async Task Names_that_are_dns_labels_are_accepted_on_create_and_rename(string name)
    {
        var created = await _manager.CreateAsync(name, null, isActive: true);
        Assert.Equal(name, created.Name);

        var other = await _manager.CreateAsync("other", null, isActive: true);
        await _manager.DeleteAsync(created.Id);
        var renamed = await _manager.UpdateAsync(other.Id, name, null);
        Assert.Equal(name, renamed.Name);
    }

    [Fact]
    public async Task A_name_of_exactly_63_characters_is_accepted()
    {
        var name = new string('a', TenantConfiguration.MaxNameLength);

        var created = await _manager.CreateAsync(name, null, isActive: true);

        Assert.Equal(63, created.Name.Length);
    }

    public static TheoryData<string?> InvalidNames => new()
    {
        null,
        "",
        " ",
        "Invalid Name!",
        "acme ",
        "acme!",
        "-acme",
        "acme-",
        "-",
        "acme_1",
        "acme.example",
        "租户",
        "acme\n",
        new string('a', 64),
    };

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task Invalid_names_are_rejected_on_create_with_code_and_placeholders(string? name)
    {
        var error = await Assert.ThrowsAsync<BusinessException>(() => _manager.CreateAsync(name!, null, isActive: true));

        Assert.Equal(MultiTenancyErrorCodes.NameInvalid, error.Code);
        Assert.Equal(name, error.LocalizationData["Name"]);
        Assert.Equal(TenantConfiguration.NamePattern, error.LocalizationData["Pattern"]);
        Assert.False(await _db.Set<TenantRecord>().AnyAsync());
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task Invalid_names_are_rejected_on_rename_and_the_old_name_is_kept(string? name)
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: true);

        var error = await Assert.ThrowsAsync<BusinessException>(() => _manager.UpdateAsync(record.Id, name!, "Acme Inc."));

        Assert.Equal(MultiTenancyErrorCodes.NameInvalid, error.Code);
        Assert.Equal("Acme", (await _store.FindAsync(record.Id))!.Name);
    }

    /// <summary>名称规则只管新写入的名称：存量租户的名称可能早于规则，不改名的编辑照常保存，改名才按规则校验。</summary>
    [Fact]
    public async Task A_pre_rule_name_can_be_kept_on_update_but_not_renamed_to_another_invalid_name()
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: true);
        await _db.Set<TenantRecord>()
            .Where(t => t.Id == record.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Name, "acme_corp")
                .SetProperty(t => t.NormalizedName, "ACME_CORP"));
        // ExecuteUpdate 绕过变更跟踪，清掉创建时跟踪的旧实体，管理器才读得到库里的存量名称
        _db.ChangeTracker.Clear();

        var updated = await _manager.UpdateAsync(record.Id, "acme_corp", "Acme Corp");
        Assert.Equal("Acme Corp", updated.DisplayName);

        var error = await Assert.ThrowsAsync<BusinessException>(() => _manager.UpdateAsync(record.Id, "acme corp", null));
        Assert.Equal(MultiTenancyErrorCodes.NameInvalid, error.Code);
    }

    [Fact]
    public async Task Update_missing_tenant_throws_not_found()
    {
        await Assert.ThrowsAsync<TenantNotFoundException>(
            () => _manager.UpdateAsync(Guid.NewGuid(), "x", null));
    }

    [Fact]
    public async Task Database_rejects_duplicate_active_name_even_bypassing_the_manager()
    {
        await _manager.CreateAsync("Acme", null, isActive: true);

        // 绕过管理器预检直接写库：唯一性由数据库的部分唯一索引兜住，
        // 这正是并发创建（两个请求同时通过预检）走到的路径
        _db.Set<TenantRecord>().Add(new TenantRecord { Name = "Acme", NormalizedName = "ACME" });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        _db.ChangeTracker.Clear();
    }

    /// <summary>真实竞争：预检通过之后、SaveChanges 落库之前，另一方抢先写入同名租户。</summary>
    /// <remarks>
    /// 用 SaveChanges 拦截器精确插入竞争写入——这是唯一能越过管理器预检的时点。
    /// 若竞争者提前提交，预检就会直接拒绝，唯一索引那条路径一次都走不到，用例空转。
    /// </remarks>
    [Fact]
    public async Task Losing_a_race_after_the_precheck_surfaces_as_duplicate_name()
    {
        var competitor = new RaceInjectingInterceptor(_connection, "Contoso", "CONTOSO");

        var racedOptions = new DbContextOptionsBuilder<TestTenantDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(competitor)
            .Options;

        await using var racedDb = new TestTenantDbContext(racedOptions);
        var racedManager = new EfCoreTenantManager<TestTenantDbContext>(
            new FixedDbContextProvider<TestTenantDbContext>(racedDb),
            new UpperInvariantTenantNormalizer(),
            new UtcClockProvider());

        // 预检时库中无同名租户 → 通过；拦截器在 flush 前写入同名行 → 唯一索引拒绝本次插入
        await Assert.ThrowsAsync<DuplicateTenantNameException>(() => racedManager.CreateAsync("Contoso", null, isActive: true));
        Assert.True(competitor.Injected, "The competing write was not injected; the database conflict path was not exercised");
    }

    [Fact]
    public async Task Other_constraint_failures_are_not_reported_as_duplicate_name()
    {
        // 名称未变时，同名查询会命中自己；其他约束失败不能据此误报成名称冲突。
        var record = await _manager.CreateAsync("Acme", null, isActive: true);

        var tooLongDisplayName = new string('x', TestTenantDbContext.DisplayNameCheckLimit + 1);
        var ex = await Record.ExceptionAsync(() => _manager.UpdateAsync(record.Id, "Acme", tooLongDisplayName));

        Assert.NotNull(ex);
        Assert.IsNotType<DuplicateTenantNameException>(ex);

        // 失败的修改必须从跟踪器里回滚：留着它，下一次保存就会把这个已知失败的值写出去
        var tracked = await _db.Set<TenantRecord>().FirstAsync(t => t.Id == record.Id);
        Assert.Equal(EntityState.Unchanged, _db.Entry(tracked).State);
        Assert.Null(tracked.DisplayName);

        await _db.SaveChangesAsync();
        Assert.Null((await _manager.FindAsync(record.Id))!.DisplayName);
    }

    /// <summary>管理器的 DbContext 可能就是宿主的工作单元：名称冲突不能连带丢掉调用方尚未提交的业务变更。</summary>
    /// <remarks>
    /// 名称冲突只丢弃本次租户变更，不能 <c>ChangeTracker.Clear()</c> 清掉其他待提交实体。
    /// 必须模拟预检通过后被唯一索引拒绝的竞争；预检拒绝不会调用 SaveChanges，测不到保存失败的丢弃逻辑。
    /// </remarks>
    [Fact]
    public async Task Name_conflict_does_not_discard_unrelated_pending_changes()
    {
        var competitor = new RaceInjectingInterceptor(_connection, "Contoso", "CONTOSO");

        var racedOptions = new DbContextOptionsBuilder<TestTenantDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(competitor)
            .Options;

        await using var racedDb = new TestTenantDbContext(racedOptions);
        var racedManager = new EfCoreTenantManager<TestTenantDbContext>(
            new FixedDbContextProvider<TestTenantDbContext>(racedDb),
            new UpperInvariantTenantNormalizer(),
            new UtcClockProvider());

        // 调用方在同一 DbContext（= 宿主工作单元）里改了业务实体，尚未提交
        var note = new TestNote { Text = "caller's pending work" };
        racedDb.Add(note);

        await Assert.ThrowsAsync<DuplicateTenantNameException>(() => racedManager.CreateAsync("Contoso", null, isActive: true));
        Assert.True(competitor.Injected, "The competing write was not injected; the database conflict path was not exercised");

        // 无关实体仍在跟踪器里等待提交，且能正常落库
        Assert.Equal(EntityState.Added, racedDb.Entry(note).State);
        await racedDb.SaveChangesAsync();
        Assert.Equal("caller's pending work", (await racedDb.Set<TestNote>().SingleAsync()).Text);
    }

    /// <summary>预检拒绝时实体完全未被触碰——赋值必须发生在预检之后。</summary>
    /// <remarks>
    /// 若把赋值提到预检之前，被拒绝的改名会留在跟踪器里，下一次保存就把它写出去了。
    /// 保存失败路径的回滚由 <c>Other_constraint_failures_are_not_reported_as_duplicate_name</c> 覆盖。
    /// </remarks>
    [Fact]
    public async Task Precheck_rejection_leaves_the_record_untouched()
    {
        var target = await _manager.CreateAsync("Acme", null, isActive: true);
        await _manager.CreateAsync("Contoso", null, isActive: true);

        // 改成已被占用的名字：预检就会拒绝，实体上的赋值必须回滚
        await Assert.ThrowsAsync<DuplicateTenantNameException>(
            () => _manager.UpdateAsync(target.Id, "Contoso", null));

        // 跟踪器里不应残留"Contoso"这个改名意图
        await _db.SaveChangesAsync();
        var reloaded = await _manager.FindAsync(target.Id);
        Assert.Equal("Acme", reloaded!.Name);
    }

    /// <summary>删除在提交那一刻即不可达——这条路径无法靠重试补救，访问控制不能依赖事后失效。</summary>
    /// <remarks>直接读库保证提交即不可达；软删后重试删除会被 <c>!IsDeleted</c> 查询拒绝，不能依赖事后缓存失效补救。</remarks>
    [Fact]
    public async Task Deletion_takes_effect_immediately_and_needs_no_compensating_action()
    {
        var record = await _manager.CreateAsync("Acme", null, isActive: true);
        Assert.NotNull(await _store.FindAsync(record.Id));
        Assert.NotNull(await _store.FindByNameAsync("ACME"));

        await _manager.DeleteAsync(record.Id);

        Assert.Null(await _store.FindAsync(record.Id));
        Assert.Null(await _store.FindByNameAsync("ACME"));

        // 重试删除抛 NotFound；第一次提交已生效，不依赖再次删除补救。
        await Assert.ThrowsAsync<TenantNotFoundException>(() => _manager.DeleteAsync(record.Id));
    }

    [Fact]
    public async Task Name_is_reusable_after_deletion()
    {
        var first = await _manager.CreateAsync("Acme", null, isActive: true);
        await _manager.DeleteAsync(first.Id);

        // 部分唯一索引只约束未删除行
        var recreated = await _manager.CreateAsync("Acme", null, isActive: true);
        Assert.NotEqual(first.Id, recreated.Id);
    }

    /// <summary>在被测 DbContext 执行 SaveChanges 之前，用另一个连接抢先写入同名租户。</summary>
    private sealed class RaceInjectingInterceptor(
        SqliteConnection connection,
        string name,
        string normalizedName) : SaveChangesInterceptor
    {
        internal bool Injected { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Injected)
            {
                Injected = true;

                var options = new DbContextOptionsBuilder<TestTenantDbContext>()
                    .UseSqlite(connection)
                    .Options;

                await using var competitor = new TestTenantDbContext(options);
                competitor.Set<TenantRecord>().Add(new TenantRecord { Name = name, NormalizedName = normalizedName });
                await competitor.SaveChangesAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Paged_query_filters_keyword_and_excludes_deleted()
    {
        await _manager.CreateAsync("Acme", "Acme Inc.", isActive: true);
        await _manager.CreateAsync("Contoso", null, isActive: true);
        var deleted = await _manager.CreateAsync("Acme-Old", null, isActive: true);
        await _manager.DeleteAsync(deleted.Id);

        var all = await _manager.GetPagedAsync(null, null, new PageRequest { Offset = 0, Limit = 10 });
        Assert.Equal(2, all.TotalCount);

        // 关键字大小写不敏感（按归一化名称匹配），软删行不计入
        var filtered = await _manager.GetPagedAsync("acme", null, new PageRequest { Offset = 0, Limit = 10 });
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal("Acme", filtered.Items.Single().Name);

        var paged = await _manager.GetPagedAsync(null, null, new PageRequest { Offset = 1, Limit = 1 });
        Assert.Equal(2, paged.TotalCount);
        Assert.Single(paged.Items);
    }

    /// <summary>按启用状态筛选与关键字取交集；已删除的停用租户不计入停用数。</summary>
    [Fact]
    public async Task Paged_query_filters_by_activation_state()
    {
        await _manager.CreateAsync("Acme", null, isActive: true);
        await _manager.CreateAsync("Acme-Paused", null, isActive: false);
        await _manager.CreateAsync("Contoso", null, isActive: false);
        var deleted = await _manager.CreateAsync("Fabrikam", null, isActive: false);
        await _manager.DeleteAsync(deleted.Id);

        var page = new PageRequest { Offset = 0, Limit = 10 };

        Assert.Equal(["Contoso", "Acme-Paused"], (await _manager.GetPagedAsync(null, false, page)).Items.Select(t => t.Name));
        Assert.Equal(["Acme"], (await _manager.GetPagedAsync(null, true, page)).Items.Select(t => t.Name));
        Assert.Equal(["Acme-Paused"], (await _manager.GetPagedAsync("acme", false, page)).Items.Select(t => t.Name));
    }
}
