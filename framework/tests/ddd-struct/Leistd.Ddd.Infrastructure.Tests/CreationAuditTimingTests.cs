using System.Security.Claims;
using Leistd.Auditing.EntityFrameworkCore;
using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.Auditing.EntityFrameworkCore.Interceptors;
using Leistd.Timing;
using Leistd.Auditing.EntityFrameworkCore.Extensions;
using Leistd.Auditing.Abstractions;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>创建审计的落值时机：必须在实体进入跟踪时，不是保存时。</summary>
/// <remarks>
/// <para>保护的缺陷形状：若在 <c>AuditSaveChangesInterceptor.SavingChanges</c> 里落值，
/// 由于仓储在工作单元内不立即保存，新增与保存之间可以跨越
/// <c>ICurrentPrincipalAccessor.Change</c> 的边界——于是 <c>CreatorId</c> 落成外层主体，
/// 不报错。<c>TenantId</c> 的落值时机是同一个缺陷形状，只是字段不同。</para>
/// <para>另一半同等重要：提早落值不能碰查询出来的实体。三层护栏
/// （<c>FromQuery</c>、<c>State == Added</c>、值已有则不动）各有一条用例。</para>
/// </remarks>
public class CreationAuditTimingTests
{
    private static readonly Guid ScopeUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OuterUser = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Creation_audit_lands_when_the_entity_enters_tracking_not_at_save()
    {
        var (db, accessor, sp) = Build();
        using var _ = sp;
        using var __ = db;

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            db.Items.Add(new TestEntity("tracked-in-scope"));
        }

        // 还没有 SaveChanges：值必须已经在了。落在保存时的话这里是 null，
        // 保存前读它的代码（领域事件、校验、导出）看到的就是空
        var entity = db.ChangeTracker.Entries<TestEntity>().Single().Entity;
        Assert.Equal(ScopeUser.ToString(), entity.CreatorId);
        Assert.NotEqual(default, entity.CreationTime);
    }

    [Fact]
    public async Task Entity_added_inside_a_principal_scope_keeps_that_creator_when_saved_later()
    {
        var (db, accessor, sp) = Build();
        using var _ = sp;
        using var __ = db;

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            db.Items.Add(new TestEntity("added-in-scope"));
        }

        // 作用域已退出，改由外层主体提交——模拟 UoW 统一提交
        using (accessor.Change(PrincipalOf(OuterUser)))
        {
            await db.SaveChangesAsync();
        }

        var saved = await db.Items.SingleAsync();
        Assert.Equal(ScopeUser.ToString(), saved.CreatorId);
    }

    [Fact]
    public async Task Creation_audit_of_a_queried_entity_is_not_overwritten()
    {
        var (db, accessor, sp) = Build(out var options);
        using var _ = sp;

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            db.Items.Add(new TestEntity("created-once"));
            await db.SaveChangesAsync();
        }

        await db.DisposeAsync();

        // 换一个上下文、换一个主体重新查出来：查询物化不得触发落值
        using var reread = new TestDbContext(options, sp);
        TestEntity queried;
        using (accessor.Change(PrincipalOf(OuterUser)))
        {
            queried = await reread.Items.SingleAsync();
            queried.Rename("renamed-by-outer");
            await reread.SaveChangesAsync();
        }

        Assert.Equal(ScopeUser.ToString(), queried.CreatorId);
        Assert.Equal(OuterUser.ToString(), queried.LastModifierId);
    }

    [Fact]
    public void Explicitly_assigned_creation_audit_is_not_overwritten()
    {
        var (db, accessor, sp) = Build();
        using var _ = sp;
        using var __ = db;

        var seeded = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new TestEntity("seeded");
        entity.AssignCreationAudit(OuterUser.ToString(), seeded);

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            db.Items.Add(entity);
        }

        // 种子/导入/迁移显式赋过的值不动
        Assert.Equal(OuterUser.ToString(), entity.CreatorId);
        Assert.Equal(seeded, entity.CreationTime);
    }

    [Fact]
    public void Entity_switched_to_added_after_tracking_still_gets_creation_audit()
    {
        var (db, accessor, sp) = Build();
        using var _ = sp;
        using var __ = db;

        var entity = new TestEntity("attached-then-added");
        db.Items.Attach(entity);                 // 以 Unchanged 进入跟踪，不触发落值
        Assert.Null(entity.CreatorId);

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            // upsert 类写法：跟踪后才改成 Added。Tracked 事件已经触发完了，
            // 只有 StateChanged 能看到这次迁移——只订阅 Tracked 会在此漏值
            db.Entry(entity).State = EntityState.Added;
        }

        Assert.Equal(ScopeUser.ToString(), entity.CreatorId);
    }

    /// <summary>普通 <see cref="DbContext"/> 用同一个原语接创建审计，时机与基座一致。</summary>
    /// <remarks>
    /// <para>控制面上下文（如租户注册表）刻意不继承 <c>BaseDbContext</c>——控制面数据是宿主侧的，
    /// 不能被租户过滤器作用，否则解析租户就要先知道租户。代价是拿不到基座的钩子。</para>
    /// <para>创建审计从拦截器移到"进入跟踪"之后，普通上下文一度完全没有创建审计：
    /// 拦截器不再处理 <c>Added</c>，而它又没有基座的钩子。
    /// <c>EnableCreationAuditing</c> 就是补这个缺口，本用例锁住它。</para>
    /// </remarks>
    [Fact]
    public void Plain_db_context_gets_creation_audit_at_tracking_time()
    {
        var (db, accessor, sp) = BuildPlain();
        using var _ = sp;
        using var __ = db;

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            db.Items.Add(new TestEntity("control-plane-row"));
        }

        // 与基座同一时机：SaveChanges 之前值就该在
        var entity = db.ChangeTracker.Entries<TestEntity>().Single().Entity;
        Assert.Equal(ScopeUser.ToString(), entity.CreatorId);
        Assert.NotEqual(default, entity.CreationTime);
    }

    [Fact]
    public async Task Plain_db_context_keeps_the_adding_principal_when_saved_later()
    {
        var (db, accessor, sp) = BuildPlain();
        using var _ = sp;
        using var __ = db;

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            db.Items.Add(new TestEntity("added-in-scope"));
        }

        using (accessor.Change(PrincipalOf(OuterUser)))
        {
            await db.SaveChangesAsync();
        }

        var saved = await db.Items.SingleAsync();
        Assert.Equal(ScopeUser.ToString(), saved.CreatorId);
    }

    /// <summary>未注册审计组件时不订阅，不抛异常</summary>
    [Fact]
    public void Plain_db_context_without_an_audit_setter_simply_does_not_stamp()
    {
        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase($"audit-none-{Guid.NewGuid()}")
            .Options;

        using var db = new PlainDbContext(options, serviceProvider: null);
        var entity = new TestEntity("no-auditing-host");
        db.Items.Add(entity);

        Assert.Null(entity.CreatorId);
    }

    /// <summary>直接把 <c>IsDeleted</c> 翻成 true 也必须落删除审计。</summary>
    /// <remarks>
    /// <para>软删除有两条到达路径：<c>Remove()</c>（状态变 <c>Deleted</c>，拦截器转换）与
    /// 直接翻标志（状态就是 <c>Modified</c>）。拦截器若对 <c>IsDeleted:true</c> 的
    /// <c>Modified</c> 一律跳过，第二条路径就<b>既没有修改审计也没有删除审计</b>，
    /// <c>DeleterId</c> 恒为 null 且没有任何信号。</para>
    /// <para>管理器为了不依赖宿主挂拦截器而直接翻标志，正落在这一档。</para>
    /// </remarks>
    [Fact]
    public async Task Flipping_the_soft_delete_flag_records_deletion_audit()
    {
        var (db, accessor, sp) = BuildSoftDelete();
        using var _ = sp;
        using var __ = db;

        var entity = new SoftDeletableEntity("to-be-deleted");
        db.Items.Add(entity);
        await db.SaveChangesAsync();

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            entity.MarkDeleted();          // 不经 Remove()：直接翻标志
            await db.SaveChangesAsync();
        }

        Assert.True(entity.IsDeleted);
        Assert.Equal(ScopeUser.ToString(), entity.DeleterId);
        Assert.NotNull(entity.DeletionTime);
    }

    /// <summary>Remove() 路径不受影响</summary>
    [Fact]
    public async Task Removing_a_soft_deletable_entity_still_records_deletion_audit()
    {
        var (db, accessor, sp) = BuildSoftDelete();
        using var _ = sp;
        using var __ = db;

        var entity = new SoftDeletableEntity("removed");
        db.Items.Add(entity);
        await db.SaveChangesAsync();

        using (accessor.Change(PrincipalOf(ScopeUser)))
        {
            db.Items.Remove(entity);
            await db.SaveChangesAsync();
        }

        Assert.True(entity.IsDeleted);
        Assert.Equal(ScopeUser.ToString(), entity.DeleterId);
    }

    /// <summary>无主体删除留下的空 <c>DeleterId</c>，不得被后来的修改者顶替。</summary>
    /// <remarks>
    /// <para>各字段落值本身幂等（已有值就跳过），所以"已删除实体又被改"在大多数字段上本来就安全。
    /// 真正会失真的是这一种：删除发生在没有主体的上下文（后台作业、迁移脚本），
    /// <c>DeleterId</c> 留空；之后任何一个真人修改这条已删除记录，
    /// 若仍按"当前值为已删除"就执行删除审计，那个人就被填成删除者。</para>
    /// <para>于是"谁删的"会得到一个确定但错误的答案——比留空危险得多，
    /// 因为它看起来是可信的。判据必须是 <c>OriginalValue</c> 的 false→true 迁移。</para>
    /// </remarks>
    [Fact]
    public async Task A_later_editor_does_not_become_the_deleter_of_a_system_deleted_row()
    {
        var (db, accessor, sp) = BuildSoftDelete();
        using var _ = sp;
        using var __ = db;

        var entity = new SoftDeletableEntity("system-deleted");
        db.Items.Add(entity);
        await db.SaveChangesAsync();

        // 无主体上下文删除：DeleterId 无从可知，留空
        entity.MarkDeleted();
        await db.SaveChangesAsync();
        Assert.True(entity.IsDeleted);
        Assert.Null(entity.DeleterId);

        // 之后真人修改这条已删除记录
        using (accessor.Change(PrincipalOf(OuterUser)))
        {
            entity.Rename("edited-after-deletion");
            await db.SaveChangesAsync();
        }

        // 他只是改过它，不是删它的人
        Assert.Null(entity.DeleterId);
    }

    /// <summary>未注册 <c>ICurrentUser</c> 的宿主仍能解析审计设施并完成匿名审计。</summary>
    /// <remarks>
    /// <para>迁移作业、后台任务、设计时工具都没有请求主体。把 <c>ICurrentUser</c> 设成必需依赖时，
    /// 这些宿主在配置 DbContext 的那一刻就失败——错误信息指向审计设施，
    /// 与它们真正在做的事（迁移）毫无关系。</para>
    /// <para>匿名语义：时间审计照常落值，用户字段留空。留空表示"未知"，
    /// 比为了让它非空而伪造一个身份诚实。</para>
    /// </remarks>
    [Fact]
    public async Task A_host_without_a_current_user_still_resolves_and_audits_anonymously()
    {
        var sp = new ServiceCollection()
            .AddSingleton<IClock, UtcClockProvider>()
            .AddAuditingEfCore()          // 刻意不注册 ICurrentUser / ICurrentPrincipalAccessor
            .BuildServiceProvider();

        // 解析本身必须成功：这正是迁移作业配置 DbContext 时走的那一步
        var interceptor = sp.GetRequiredService<AuditSaveChangesInterceptor>();

        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase($"audit-anonymous-{Guid.NewGuid()}")
            .AddInterceptors(interceptor)
            .Options;

        using var db = new PlainDbContext(options, sp);
        var entity = new TestEntity("created-by-a-background-job");
        db.Items.Add(entity);
        await db.SaveChangesAsync();

        entity.Rename("edited-by-a-background-job");
        await db.SaveChangesAsync();

        Assert.NotEqual(default, entity.CreationTime);
        Assert.NotNull(entity.LastModificationTime);
        Assert.Null(entity.CreatorId);
        Assert.Null(entity.LastModifierId);
    }

    #region 装配

    private static (TestDbContext db, ICurrentPrincipalAccessor accessor, ServiceProvider sp) Build() =>
        Build(out _);

    private static (TestDbContext db, ICurrentPrincipalAccessor accessor, ServiceProvider sp) Build(
        out DbContextOptions options)
    {
        var sp = new ServiceCollection()
            .AddSingleton<IClock, UtcClockProvider>()
            .AddSingleton<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>()
            .AddOptions().AddSingleton<ICurrentUser, CurrentUser>()
            .AddAuditingEfCore()
            .BuildServiceProvider();

        // 修改/删除审计仍在保存时刻，拦截器照常挂载——本测试也顺带锁住这条分界线
        options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase($"audit-{Guid.NewGuid()}")
            .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>())
            .Options;

        return (new TestDbContext(options, sp), sp.GetRequiredService<ICurrentPrincipalAccessor>(), sp);
    }

    private static ClaimsPrincipal PrincipalOf(Guid userId) =>
        new(new ClaimsIdentity([new Claim("sub", userId.ToString())], "Test"));


    private sealed class TestEntity : Entity<Guid>, ICreationAuditedObject, IModificationAuditedObject
    {
        public string Name { get; private set; } = "";
        public DateTime CreationTime { get; private set; }
        public string? CreatorId { get; private set; }
        public DateTime? LastModificationTime { get; private set; }
        public string? LastModifierId { get; private set; }

        private TestEntity() { }

        public TestEntity(string name)
        {
            Id = Guid.NewGuid();
            Name = name;
        }

        public void Rename(string name) => Name = name;

        public void AssignCreationAudit(string creatorId, DateTime creationTime)
        {
            CreatorId = creatorId;
            CreationTime = creationTime;
        }
    }

    private static (PlainDbContext db, ICurrentPrincipalAccessor accessor, ServiceProvider sp) BuildPlain()
    {
        var sp = new ServiceCollection()
            .AddSingleton<IClock, UtcClockProvider>()
            .AddSingleton<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>()
            .AddOptions().AddSingleton<ICurrentUser, CurrentUser>()
            .AddAuditingEfCore()
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase($"audit-plain-{Guid.NewGuid()}")
            .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>())
            .Options;

        return (
            new PlainDbContext(options, sp),
            sp.GetRequiredService<ICurrentPrincipalAccessor>(),
            sp);
    }

    /// <summary>不继承基座的上下文，只接创建审计</summary>
    private sealed class PlainDbContext : DbContext
    {
        public PlainDbContext(DbContextOptions options, IServiceProvider? serviceProvider)
            : base(options)
        {
            ChangeTracker.EnableCreationAuditing(serviceProvider);
        }

        public DbSet<TestEntity> Items => Set<TestEntity>();
    }

    private static (SoftDeleteDbContext db, ICurrentPrincipalAccessor accessor, ServiceProvider sp) BuildSoftDelete()
    {
        var sp = new ServiceCollection()
            .AddSingleton<IClock, UtcClockProvider>()
            .AddSingleton<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>()
            .AddOptions().AddSingleton<ICurrentUser, CurrentUser>()
            .AddAuditingEfCore()
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase($"audit-softdelete-{Guid.NewGuid()}")
            .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>())
            .Options;

        return (
            new SoftDeleteDbContext(options, sp),
            sp.GetRequiredService<ICurrentPrincipalAccessor>(),
            sp);
    }

    private sealed class SoftDeletableEntity : Entity<Guid>, IDeletionAuditedObject, IModificationAuditedObject
    {
        public string Name { get; private set; } = "";
        public bool IsDeleted { get; private set; }
        public DateTime? DeletionTime { get; private set; }
        public string? DeleterId { get; private set; }
        public DateTime? LastModificationTime { get; private set; }
        public string? LastModifierId { get; private set; }

        private SoftDeletableEntity() { }

        public SoftDeletableEntity(string name)
        {
            Id = Guid.NewGuid();
            Name = name;
        }

        public void Rename(string name) => Name = name;

        /// <summary>直接翻标志，不经 <c>Remove()</c>——管理器为了不依赖拦截器就是这么写的</summary>
        public void MarkDeleted() => IsDeleted = true;
    }

    private sealed class SoftDeleteDbContext(DbContextOptions options, IServiceProvider serviceProvider)
        : BaseDbContext(options, serviceProvider)
    {
        public DbSet<SoftDeletableEntity> Items => Set<SoftDeletableEntity>();
    }

    private sealed class TestDbContext(DbContextOptions options, IServiceProvider serviceProvider)
        : BaseDbContext(options, serviceProvider)
    {
        public DbSet<TestEntity> Items => Set<TestEntity>();

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name);
                b.Property(x => x.CreationTime);
                b.Property(x => x.CreatorId);
                b.Property(x => x.LastModificationTime);
                b.Property(x => x.LastModifierId);
            });
        }
    }

    #endregion
}
