using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Errors;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.Stores;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

public class TenantConnectionConfigurationManagerTests : IAsyncLifetime
{
    private const string Name = "crm";

    private ServiceProvider _provider = default!;
    private SqliteConnection _connection = default!;
    private TestDbContext _dbContext = default!;
    private ITenantManager _tenantManager = default!;
    private ITenantConnectionConfigurationManager _manager = default!;
    private ITenantConnectionConfigurationStore _store = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMultiTenancyEfCore<TestDbContext>();

        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<TestDbContext>();
        await _dbContext.Database.EnsureCreatedAsync();
        _tenantManager = _provider.GetRequiredService<ITenantManager>();
        _manager = _provider.GetRequiredService<ITenantConnectionConfigurationManager>();
        _store = _provider.GetRequiredService<ITenantConnectionConfigurationStore>();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_registration_requires_a_connection_string(string connectionString)
    {
        var tenant = await CreateTenantAsync();

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => _manager.SetAsync(tenant.Id, Name, connectionString, expectedVersion: null));
        Assert.Equal(MultiTenancyErrorCodes.ConnectionStringInvalid, error.Code);
    }

    [Fact]
    public async Task Connection_strings_longer_than_the_limit_are_rejected()
    {
        var tenant = await CreateTenantAsync();

        var error = await Assert.ThrowsAsync<BusinessException>(() => _manager.SetAsync(
            tenant.Id,
            Name,
            new string('x', TenantConnectionConfiguration.MaxConnectionStringLength + 1),
            expectedVersion: null));
        Assert.Equal(MultiTenancyErrorCodes.ConnectionStringInvalid, error.Code);
    }

    // 语法都不对的连接串在登记时就拒绝：放进去要到开通阶段才由驱动抛出，用户看到的是系统级故障
    [Fact]
    public async Task Malformed_connection_strings_are_rejected_without_echoing_them()
    {
        var tenant = await CreateTenantAsync();

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => _manager.SetAsync(tenant.Id, Name, "Host=a;=secret-value", expectedVersion: null));

        Assert.Equal(MultiTenancyErrorCodes.ConnectionStringInvalid, error.Code);
        Assert.DoesNotContain("secret-value", error.ToString());
    }

    [Fact]
    public async Task The_connection_string_is_trimmed_before_it_is_stored()
    {
        var tenant = await CreateTenantAsync();

        var configuration = await _manager.SetAsync(tenant.Id, Name, "  Host=tenant  ", expectedVersion: null);

        Assert.Equal("Host=tenant", configuration.ConnectionString);
        Assert.Equal("Host=tenant", (await _store.FindAsync(tenant.Id, Name))!.Connection!.ConnectionString);
    }

    // 管理员填 Crm 还是 crm 都命中同一行，避免"大小写不同于是走到拒绝分支"的支持问题
    [Theory]
    [InlineData("Crm")]
    [InlineData("CRM")]
    [InlineData("  crm  ")]
    public async Task Names_are_normalized_to_lower_case(string name)
    {
        var tenant = await CreateTenantAsync();

        var configuration = await _manager.SetAsync(tenant.Id, name, "Host=tenant", expectedVersion: null);

        Assert.Equal("crm", configuration.Name);
        Assert.Equal("crm", (await _store.FindAsync(tenant.Id, "crm"))!.Connection!.Name);
    }

    [Theory]
    [InlineData("crm_db")]
    [InlineData("crm db")]
    [InlineData("crm.db")]
    [InlineData("")]
    public async Task Invalid_names_are_rejected(string name)
    {
        var tenant = await CreateTenantAsync();

        // 名字来自管理员输入：400 而不是 500
        var error = await Assert.ThrowsAsync<BusinessException>(
            () => _manager.SetAsync(tenant.Id, name, "Host=tenant", expectedVersion: null));
        Assert.Equal(MultiTenancyErrorCodes.ConnectionNameInvalid, error.Code);
    }

    // 同一租户的不同名字是各自独立的行，各有各的版本
    [Fact]
    public async Task Registrations_of_one_tenant_are_independent_per_name()
    {
        var tenant = await CreateTenantAsync();

        var crm = await _manager.SetAsync(tenant.Id, "crm", "Host=crm", expectedVersion: null);
        var foundation = await _manager.SetAsync(tenant.Id, "foundation", "Host=foundation", expectedVersion: null);

        Assert.Equal(1, crm.Version);
        Assert.Equal(1, foundation.Version);
        Assert.Equal(2, await _dbContext.Set<TenantConnectionRecord>().CountAsync(x => x.TenantId == tenant.Id));
    }

    /// <summary>创建时间与修改时间由管理器填充，不依赖宿主是否接入审计层。</summary>
    /// <remarks>
    /// 这一行决定该租户在这个服务的数据落在哪个库，所以"配置在何时被改过"必须无条件可查。
    /// 用户字段（<c>CreatorId</c> / <c>LastModifierId</c>）由宿主审计层补充：控制面上下文
    /// 用 <c>ChangeTracker.EnableCreationAuditing</c> 接上同一时机原语，本测试的上下文没接，
    /// 因此这里只钉时间字段。
    /// </remarks>
    [Fact]
    public async Task Set_fills_creation_and_modification_times()
    {
        var tenant = await CreateTenantAsync();

        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);

        var created = await ReadRecordAsync(tenant.Id);
        Assert.NotEqual(default, created.CreationTime);
        Assert.Null(created.LastModificationTime);

        await _manager.SetAsync(tenant.Id, Name, "Host=two", expectedVersion: 1);

        var updated = await ReadRecordAsync(tenant.Id);
        Assert.Equal(created.CreationTime, updated.CreationTime);
        Assert.NotNull(updated.LastModificationTime);
    }

    [Fact]
    public async Task Set_creates_one_record_and_updates_increment_the_version()
    {
        var tenant = await CreateTenantAsync();

        var created = await _manager.SetAsync(tenant.Id, Name, "Host=v1", expectedVersion: null);
        var updated = await _manager.SetAsync(tenant.Id, Name, "Host=v2", expectedVersion: 1);

        Assert.Equal(1, created.Version);
        Assert.Equal(2, updated.Version);
        Assert.Single(await _dbContext.Set<TenantConnectionRecord>().ToListAsync());
        Assert.Equal("Host=v2", (await _store.FindAsync(tenant.Id, Name))!.Connection!.ConnectionString);
    }

    [Fact]
    public async Task Missing_tenant_is_rejected()
    {
        await Assert.ThrowsAsync<TenantNotFoundException>(
            () => _manager.SetAsync(Guid.NewGuid(), Name, "Host=tenant", expectedVersion: null));
    }

    [Fact]
    public async Task Configuration_string_representation_does_not_expose_the_connection_string()
    {
        var tenant = await CreateTenantAsync();
        var configuration = await _manager.SetAsync(
            tenant.Id, Name, "Host=highly-sensitive-host;Password=highly-sensitive", expectedVersion: null);

        Assert.DoesNotContain("highly-sensitive", configuration.ToString(), StringComparison.Ordinal);
    }

    /// <summary>预期版本与实际状态的四种组合，只有两种放行。</summary>
    /// <remarks>
    /// 丢更新在这一行是静默的：两个管理员同时改同一个名字的连接，后提交的那次覆盖前一次且不报错，
    /// 而"该租户在这个服务的数据落在哪个库"已经变了。因此四种组合都要钉住，不能只测匹配那一种。
    /// </remarks>
    [Fact]
    public async Task Expected_version_must_match_actual_state()
    {
        var tenant = await CreateTenantAsync();

        // 预期不存在 + 实际不存在 → 放行（创建）
        var created = await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);
        Assert.Equal(1, created.Version);

        // 预期不存在 + 实际已存在 → 冲突。以为在创建，实际会覆盖别人刚登记的连接
        var createOverExisting = await Assert.ThrowsAsync<TenantConnectionVersionConflictException>(
            () => _manager.SetAsync(tenant.Id, Name, "Host=two", expectedVersion: null));
        Assert.Null(createOverExisting.ExpectedVersion);
        Assert.Equal(1, createOverExisting.ActualVersion);

        // 预期陈旧版本 + 实际更新 → 冲突
        var stale = await Assert.ThrowsAsync<TenantConnectionVersionConflictException>(
            () => _manager.SetAsync(tenant.Id, Name, "Host=two", expectedVersion: 99));
        Assert.Equal(99, stale.ExpectedVersion);
        Assert.Equal(1, stale.ActualVersion);

        // 预期版本 + 实际相符 → 放行（更新）
        var updated = await _manager.SetAsync(tenant.Id, Name, "Host=two", expectedVersion: 1);
        Assert.Equal(2, updated.Version);
    }

    /// <summary>冲突时不得留下任何写入</summary>
    [Fact]
    public async Task Version_conflict_leaves_the_stored_registration_untouched()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);

        await Assert.ThrowsAsync<TenantConnectionVersionConflictException>(
            () => _manager.SetAsync(tenant.Id, Name, "Host=hijack", expectedVersion: 42));

        var stored = (await _store.FindAsync(tenant.Id, Name))!.Connection!;
        Assert.Equal("Host=one", stored.ConnectionString);
        Assert.Equal(1, stored.Version);
    }

    /// <summary>在用租户不允许改连接。</summary>
    /// <remarks>
    /// 路由改了不等于数据跟着走：资源服务按租户与连接名缓存解析结果，已缓存的实例继续写旧库、
    /// 冷启动的实例开始写新库，同一租户在两个物理位置同时产生新数据，且双方都不报错。
    /// 这是数据分叉，不是传播延迟，所以必须在唯一写入口上挡住，而不是靠流程约束。
    /// </remarks>
    [Fact]
    public async Task Changing_a_registration_of_an_active_tenant_is_rejected()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);
        await _tenantManager.SetActiveAsync(tenant.Id, true);

        var rejected = await Assert.ThrowsAsync<TenantConnectionChangeRequiresInactiveTenantException>(
            () => _manager.SetAsync(tenant.Id, Name, "Host=moved", expectedVersion: 1));
        Assert.Equal(tenant.Id, rejected.TenantId);

        var stored = (await _store.FindAsync(tenant.Id, Name))!.Connection!;
        Assert.Equal("Host=one", stored.ConnectionString);
        Assert.Equal(1, stored.Version);
    }

    /// <summary>删除同样是改路由：在用租户不允许删</summary>
    [Fact]
    public async Task Removing_a_registration_of_an_active_tenant_is_rejected()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);
        await _tenantManager.SetActiveAsync(tenant.Id, true);

        await Assert.ThrowsAsync<TenantConnectionChangeRequiresInactiveTenantException>(
            () => _manager.RemoveAsync(tenant.Id, Name, expectedVersion: 1));

        Assert.NotNull((await _store.FindAsync(tenant.Id, Name))!.Connection);
    }

    /// <summary>停用租户后允许切换连接。</summary>
    [Fact]
    public async Task Changing_a_registration_is_allowed_once_the_tenant_is_deactivated()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);
        await _tenantManager.SetActiveAsync(tenant.Id, true);
        await _tenantManager.SetActiveAsync(tenant.Id, false);

        var moved = await _manager.SetAsync(tenant.Id, Name, "Host=moved", expectedVersion: 1);

        Assert.Equal("Host=moved", moved.ConnectionString);
        Assert.Equal(2, moved.Version);
    }

    /// <summary>把"不分库"的在用租户改成分库，必须先停用。</summary>
    /// <remarks>首次登记同样改变数据落点，既有种子、管理员和业务数据不会迁移，故不能按“首次写入”豁免停用要求。</remarks>
    [Fact]
    public async Task The_first_connection_of_an_active_tenant_is_rejected()
    {
        var tenant = await CreateTenantAsync();
        await _tenantManager.SetActiveAsync(tenant.Id, true);

        var rejected = await Assert.ThrowsAsync<TenantConnectionChangeRequiresInactiveTenantException>(
            () => _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null));
        Assert.Equal(tenant.Id, rejected.TenantId);

        // 拒绝之后不得留下任何登记：留下半条就等于悄悄分了库
        Assert.Empty(await _dbContext.Set<TenantConnectionRecord>().AsNoTracking().ToListAsync());
    }

    /// <summary>分库租户启用时允许补登缺失的连接名。</summary>
    /// <remarks>缺名时 <c>TenantConnectionTargets.Select</c> 失败关闭，没有向默认配置库写入数据；补登不改变既有数据落点，无需停用。</remarks>
    [Fact]
    public async Task Adding_a_missing_name_to_a_sharded_tenant_is_allowed_while_it_is_active()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, "default", "Host=one-db", expectedVersion: null);
        await _tenantManager.SetActiveAsync(tenant.Id, true);

        var added = await _manager.SetAsync(tenant.Id, Name, "Host=crm-db", expectedVersion: null);

        Assert.Equal(1, added.Version);
        Assert.Equal("Host=crm-db", (await _store.FindAsync(tenant.Id, Name))!.Connection!.ConnectionString);
    }

    // 删除把租户从分库改回不分库：该名字之后回落到服务自己的配置
    [Fact]
    public async Task Removing_the_registration_makes_the_tenant_fall_back_again()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);

        await _manager.RemoveAsync(tenant.Id, Name, expectedVersion: 1);

        var lookup = await _store.FindAsync(tenant.Id, Name);
        Assert.False(lookup!.HasAnyConnection);
        Assert.Empty(await _dbContext.Set<TenantConnectionRecord>().Where(x => x.TenantId == tenant.Id).ToListAsync());
    }

    [Fact]
    public async Task Removing_with_a_stale_or_missing_version_conflicts()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);

        var stale = await Assert.ThrowsAsync<TenantConnectionVersionConflictException>(
            () => _manager.RemoveAsync(tenant.Id, Name, expectedVersion: 42));
        Assert.Equal(1, stale.ActualVersion);

        // 删一个根本不存在的名字同样是冲突：调用方以为自己读到过它
        var missing = await Assert.ThrowsAsync<TenantConnectionVersionConflictException>(
            () => _manager.RemoveAsync(tenant.Id, "foundation", expectedVersion: 1));
        Assert.Null(missing.ActualVersion);

        Assert.NotNull((await _store.FindAsync(tenant.Id, Name))!.Connection);
    }

    /// <summary>真正的同时提交也必须是 409，不能是 500。</summary>
    /// <remarks>
    /// <para><c>expectedVersion</c> 预检只挡得住"读到的版本已过期"这种串行陈旧。真正的竞态是
    /// 两个事务都先读到 v1：双方预检都过，先提交的把版本推到 v2，后提交的并发令牌命中 0 行。
    /// 数据库因此没有丢更新，但若不转换异常，调用方拿到的是 500——而契约承诺 409。</para>
    /// <para>用两个独立 DbContext 各自先把记录读进变更跟踪器来构造这个交错：manager 内部再查时
    /// 拿到的是本上下文已跟踪的实例（<c>OriginalValue</c> 仍是 v1），于是两边的预检都通过。</para>
    /// </remarks>
    [Fact]
    public async Task Simultaneous_commits_surface_as_a_version_conflict_not_a_server_error()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);

        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();

        // 屏障：两个上下文都必须在任何一方保存之前读到 v1
        await TrackAsync(scopeA, tenant.Id);
        await TrackAsync(scopeB, tenant.Id);

        var managerA = scopeA.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationManager>();
        var managerB = scopeB.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationManager>();

        var winner = await managerA.SetAsync(tenant.Id, Name, "Host=a", expectedVersion: 1);
        Assert.Equal(2, winner.Version);

        var conflict = await Assert.ThrowsAsync<TenantConnectionVersionConflictException>(
            () => managerB.SetAsync(tenant.Id, Name, "Host=b", expectedVersion: 1));

        // 报出的"实际版本"必须是重读后的真实值，好让调用方知道该以什么为基准重试
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.Equal(2, conflict.ActualVersion);
        Assert.IsType<DbUpdateConcurrencyException>(conflict.InnerException);

        // 败者的写入不得落库
        Assert.Equal("Host=a", (await _store.FindAsync(tenant.Id, Name))!.Connection!.ConnectionString);

        static async Task TrackAsync(AsyncServiceScope scope, Guid tenantId)
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await dbContext.Set<TenantConnectionRecord>().FirstOrDefaultAsync(x => x.TenantId == tenantId);
        }
    }

    /// <summary>路由事务读到"已停用"之后、写入之前，激活提交 —— 必须由数据库拦下。</summary>
    /// <remarks>
    /// 只读一次 <c>IsActive</c> 在 READ COMMITTED 下挡不住它：读不持锁，激活可以在读之后提交。
    /// 若不拦，最终租户是启用状态、热实例用旧路由、冷实例用新路由，同一租户同时往两个库写新数据。
    /// 路由侧带的是过期的租户版本，UPDATE 命中 0 行。
    /// </remarks>
    [Fact]
    public async Task Activation_committed_between_the_route_read_and_write_is_rejected()
    {
        var tenant = await CreateTenantAsync();
        await _manager.SetAsync(tenant.Id, Name, "Host=one", expectedVersion: null);

        await using var routeScope = _provider.CreateAsyncScope();
        var routeDbContext = routeScope.ServiceProvider.GetRequiredService<TestDbContext>();

        // 路由侧先读租户行：此刻 IsActive = false，检查会通过
        var trackedTenant = await routeDbContext.Set<TenantRecord>().FirstAsync(x => x.Id == tenant.Id);
        Assert.False(trackedTenant.IsActive);
        await routeDbContext.Set<TenantConnectionRecord>().FirstOrDefaultAsync(x => x.TenantId == tenant.Id);

        // 另一个上下文完成激活并提交
        await using (var activationScope = _provider.CreateAsyncScope())
        {
            await activationScope.ServiceProvider.GetRequiredService<ITenantManager>()
                .SetActiveAsync(tenant.Id, true);
        }

        // 路由侧此时才写入：它读到的租户版本已过期
        var routeManager = routeScope.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationManager>();
        await Assert.ThrowsAsync<TenantConcurrencyConflictException>(
            () => routeManager.SetAsync(tenant.Id, Name, "Host=moved", expectedVersion: 1));

        // 路由必须原样：租户已被激活，而它的数据落点没有被改动
        Assert.Equal("Host=one", (await _store.FindAsync(tenant.Id, Name))!.Connection!.ConnectionString);
    }

    /// <summary>并发首次登记：败者是 409，不是主键冲突导致的 500。</summary>
    /// <remarks>
    /// 两个请求都以 <c>expectedVersion: null</c> 登记同一个名字，都读到"这一行不存在"，
    /// 都要推进同一个租户版本 —— 败者在插入之前就冲突。靠租户版本而不是识别主键冲突：
    /// 后者是 provider 相关的，且容易把别的约束错误也误判成 409。
    /// </remarks>
    [Fact]
    public async Task Concurrent_first_registration_conflicts_instead_of_violating_the_primary_key()
    {
        var tenant = await CreateTenantAsync();

        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();

        // 屏障：两边都要在任何一方写入之前读到租户行与"这一行不存在"
        foreach (var scope in new[] { scopeA, scopeB })
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await dbContext.Set<TenantRecord>().FirstAsync(x => x.Id == tenant.Id);
            Assert.Null(await dbContext.Set<TenantConnectionRecord>()
                .FirstOrDefaultAsync(x => x.TenantId == tenant.Id));
        }

        var managerA = scopeA.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationManager>();
        var managerB = scopeB.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationManager>();

        var created = await managerA.SetAsync(tenant.Id, Name, "Host=a", expectedVersion: null);
        Assert.Equal(1, created.Version);

        // 败者重查时会看到 A 已提交的行，于是被 expectedVersion 预检拦下——
        // 报的是"预期不存在却已存在"。关键是不会落成主键冲突导致的 500
        var conflict = await Assert.ThrowsAsync<TenantConnectionVersionConflictException>(
            () => managerB.SetAsync(tenant.Id, Name, "Host=b", expectedVersion: null));
        Assert.Null(conflict.ExpectedVersion);
        Assert.Equal(1, conflict.ActualVersion);

        Assert.Single(await _dbContext.Set<TenantConnectionRecord>().AsNoTracking().ToListAsync());
    }

    /// <summary>并发冲突只丢弃本操作涉及的行，不碰同一工作单元里的其它实体。</summary>
    /// <remarks>
    /// <c>DbContext</c> 是宿主的工作单元。用 <c>ChangeTracker.Clear()</c> 收拾现场会把
    /// 调用方尚未提交的其它实体一并抹掉——调用方捕获 409 后继续处理时，
    /// 那些修改静默消失且没有任何信号。冲突处理只该管自己那两行。
    /// </remarks>
    [Fact]
    public async Task A_conflict_does_not_discard_unrelated_pending_changes()
    {
        var target = await CreateTenantAsync();
        var unrelated = await CreateTenantAsync();
        await _manager.SetAsync(target.Id, Name, "Host=one", expectedVersion: null);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        // 同一工作单元里，调用方对另一个租户做了尚未提交的修改
        var pending = await dbContext.Set<TenantRecord>().FirstAsync(x => x.Id == unrelated.Id);
        pending.DisplayName = "edited-by-caller";

        // 目标租户上制造并发冲突：另一个上下文先推进它的版本
        await dbContext.Set<TenantRecord>().FirstAsync(x => x.Id == target.Id);
        await dbContext.Set<TenantConnectionRecord>().FirstOrDefaultAsync(x => x.TenantId == target.Id);
        await using (var other = _provider.CreateAsyncScope())
        {
            await other.ServiceProvider.GetRequiredService<ITenantManager>().SetActiveAsync(target.Id, true);
        }

        var manager = scope.ServiceProvider.GetRequiredService<ITenantConnectionConfigurationManager>();
        await Assert.ThrowsAsync<TenantConcurrencyConflictException>(
            () => manager.SetAsync(target.Id, Name, "Host=x", expectedVersion: 1));

        // 调用方的那笔修改必须还在，并且仍能提交
        Assert.Equal(EntityState.Modified, dbContext.Entry(pending).State);
        await dbContext.SaveChangesAsync();

        var reread = await _dbContext.Set<TenantRecord>().AsNoTracking().FirstAsync(x => x.Id == unrelated.Id);
        Assert.Equal("edited-by-caller", reread.DisplayName);
    }

    /// <summary>陈旧改名必须是 409 并发，而不是泄漏成 500。</summary>
    /// <remarks>
    /// 改名同时参与两个竞争：名称唯一，和租户生命周期版本。<c>DbUpdateConcurrencyException</c>
    /// 继承 <c>DbUpdateException</c>，所以若把名称冲突那一档写在前面，陈旧改名会先走进名称判定；
    /// 而库里并没有别的同名租户，于是原样上抛 EF 异常——调用方拿到 500，而项目里明明已经定义了对应的 409。
    /// </remarks>
    [Fact]
    public async Task A_stale_rename_conflicts_as_concurrency_not_as_a_server_error()
    {
        var tenant = await CreateTenantAsync();

        await using var renameScope = _provider.CreateAsyncScope();
        var renameDbContext = renameScope.ServiceProvider.GetRequiredService<TestDbContext>();
        await renameDbContext.Set<TenantRecord>().FirstAsync(x => x.Id == tenant.Id);

        await using (var activationScope = _provider.CreateAsyncScope())
        {
            await activationScope.ServiceProvider.GetRequiredService<ITenantManager>()
                .SetActiveAsync(tenant.Id, true);
        }

        var renameManager = renameScope.ServiceProvider.GetRequiredService<ITenantManager>();
        await Assert.ThrowsAsync<TenantConcurrencyConflictException>(
            () => renameManager.UpdateAsync(tenant.Id, $"renamed-{Guid.NewGuid():N}", null));
    }

    /// <summary>名称确实被他人占用时仍报名称重复，不被并发那一档吞掉</summary>
    [Fact]
    public async Task A_genuine_name_collision_still_reports_a_duplicate_name()
    {
        var taken = await CreateTenantAsync();
        var subject = await CreateTenantAsync();

        await Assert.ThrowsAsync<DuplicateTenantNameException>(
            () => _tenantManager.UpdateAsync(subject.Id, taken.Name, null));
    }

    private Task<TenantConfiguration> CreateTenantAsync() =>
        _tenantManager.CreateAsync($"tenant-{Guid.NewGuid():N}", null, isActive: false);

    private Task<TenantConnectionRecord> ReadRecordAsync(Guid tenantId) =>
        _dbContext.Set<TenantConnectionRecord>().AsNoTracking().SingleAsync(x => x.TenantId == tenantId);

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureMultiTenancy();
        }
    }
}
