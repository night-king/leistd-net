using Leistd.Auditing.Abstractions;
using Leistd.Auditing.EntityFrameworkCore;
using Leistd.Auditing.EntityFrameworkCore.Extensions;
using Leistd.Auditing.EntityFrameworkCore.Interceptors;
using Leistd.Auditing.EntityFrameworkCore.Services;
using Leistd.Security.Users;
using Leistd.TestBase.Assertions;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Auditing.Tests;

/// <summary>保存时的审计与软删除转写。</summary>
public class AuditSaveChangesInterceptorTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AuditTestDbContext NewContext(out IAuditPropertySetter setter)
    {
        setter = new AuditPropertySetter(new FakeClock(Now), new FakeCurrentUser(UserId));
        var interceptor = new AuditSaveChangesInterceptor(setter);
        return new AuditTestDbContext(new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseInMemoryDatabase($"audit-int-{Guid.NewGuid():N}")
            .AddInterceptors(interceptor)
            .Options);
    }

    [Fact]
    public async Task Updating_an_entity_records_modification_audit()
    {
        using var db = NewContext(out _);
        var entity = new AuditedEntity { Name = "before" };
        db.Add(entity);
        await db.SaveChangesAsync();

        entity.Name = "after";
        await db.SaveChangesAsync();

        Assert.Equal(Now, entity.LastModificationTime);
        Assert.Equal(UserId.ToString(), entity.LastModifierId);
    }

    // 新增不走本拦截器：创建审计在实体进入跟踪时就落定，
    // 否则延迟提交会跨越当前主体作用域，把"谁创建的"记成后来的人。
    [Fact]
    public async Task Adding_an_entity_does_not_go_through_this_interceptor()
    {
        using var db = NewContext(out _);
        var entity = new AuditedEntity { Name = "new" };

        db.Add(entity);
        await db.SaveChangesAsync();

        Assert.Equal(default, entity.CreationTime);
        Assert.Null(entity.CreatorId);
    }

    // Remove() 对软删除实体必须转成 Modified，否则数据被真删。
    [Fact]
    public async Task Remove_on_a_soft_delete_entity_becomes_an_update()
    {
        using var db = NewContext(out _);
        var entity = new AuditedEntity { Name = "x" };
        db.Add(entity);
        await db.SaveChangesAsync();

        db.Remove(entity);
        await db.SaveChangesAsync();

        Assert.True(entity.IsDeleted);
        Assert.Equal(Now, entity.DeletionTime);
        Assert.Equal(UserId.ToString(), entity.DeleterId);
        Assert.Single(db.Audited.IgnoreQueryFilters());
    }

    // 直接置 IsDeleted = true 与 Remove() 必须等价——两种写法在业务代码里都有。
    [Fact]
    public async Task Setting_the_flag_directly_records_the_same_deletion_audit()
    {
        using var db = NewContext(out _);
        var entity = new AuditedEntity { Name = "x" };
        db.Add(entity);
        await db.SaveChangesAsync();

        entity.IsDeleted = true;
        await db.SaveChangesAsync();

        Assert.Equal(Now, entity.DeletionTime);
        Assert.Equal(UserId.ToString(), entity.DeleterId);
        // 删除审计取代同次保存的修改审计
        Assert.Null(entity.LastModificationTime);
    }

    // 已删除实体的后续编辑不得改写删除者：只识别本次 false→true 的迁移。
    [Fact]
    public async Task Editing_an_already_deleted_entity_keeps_the_original_deleter()
    {
        using var db = NewContext(out _);
        var entity = new AuditedEntity { Name = "x" };
        db.Add(entity);
        await db.SaveChangesAsync();
        entity.IsDeleted = true;
        await db.SaveChangesAsync();
        var firstDeletion = entity.DeletionTime;

        entity.Name = "edited after deletion";
        await db.SaveChangesAsync();

        Assert.Equal(firstDeletion, entity.DeletionTime);
        Assert.Equal(UserId.ToString(), entity.DeleterId);
    }

    // 不实现 ISoftDelete 的实体按硬删除处理，不能被拦截器改写状态。
    [Fact]
    public async Task Entities_without_soft_delete_are_really_removed()
    {
        using var db = NewContext(out _);
        var entity = new PlainEntity { Name = "x" };
        db.Add(entity);
        await db.SaveChangesAsync();

        db.Remove(entity);
        await db.SaveChangesAsync();

        Assert.Empty(db.Plain);
    }

    // 同步保存路径与异步走同一段逻辑，两条都要钉——只覆盖一条时另一条改坏不会红。
    [Fact]
    public void Synchronous_save_applies_the_same_audit()
    {
        using var db = NewContext(out _);
        var entity = new AuditedEntity { Name = "before" };
        db.Add(entity);
        db.SaveChanges();

        entity.Name = "after";
        db.SaveChanges();

        Assert.Equal(Now, entity.LastModificationTime);
    }

    [Fact]
    public void Registration_exposes_the_setter_and_the_interceptor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FakeClock(Now));

        services.AddAuditingEfCore();

        services.AssertSingle<IAuditPropertySetter>(ServiceLifetime.Transient);
        services.AssertSingle<AuditSaveChangesInterceptor>(ServiceLifetime.Transient);
    }

    // ICurrentUser 未注册时注册面必须仍能解析——这是"审计不拖进身份栈"的落点。
    [Fact]
    public void Setter_resolves_without_a_registered_current_user()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IClock>(new FakeClock(Now))
            .AddAuditingEfCore()
            .BuildServiceProvider();

        Assert.Null(provider.GetService<ICurrentUser>());
        Assert.NotNull(provider.GetRequiredService<IAuditPropertySetter>());
    }

    // 没有容器的场景（设计时工具、直接 new 出上下文的批处理）不订阅，也不该抛。
    [Fact]
    public void Creation_auditing_without_a_service_provider_is_a_no_op()
    {
        using var db = new AuditTestDbContext(new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseInMemoryDatabase($"audit-noop-{Guid.NewGuid():N}").Options);

        db.ChangeTracker.EnableCreationAuditing(null);
        var entity = new AuditedEntity();
        db.Add(entity);
        db.SaveChanges();

        Assert.Equal(default, entity.CreationTime);
    }

    // 查询物化出来的实体不得触发创建审计，否则每次读取都会改写历史。
    [Fact]
    public async Task Entities_materialized_from_a_query_do_not_get_creation_audit()
    {
        var name = $"db-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AuditTestDbContext>().UseInMemoryDatabase(name).Options;
        var provider = new ServiceCollection()
            .AddSingleton<IClock>(new FakeClock(Now))
            .AddAuditingEfCore()
            .BuildServiceProvider();

        var seeded = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var seed = new AuditTestDbContext(options))
        {
            seed.Add(new AuditedEntity { Name = "existing", CreationTime = seeded });
            await seed.SaveChangesAsync();
        }

        using var db = new AuditTestDbContext(options);
        db.ChangeTracker.EnableCreationAuditing(provider);

        var loaded = await db.Audited.SingleAsync();

        Assert.Equal(seeded, loaded.CreationTime);
    }

    [Fact]
    public void Change_tracker_hook_rejects_null_arguments()
    {
        using var db = new AuditTestDbContext(new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseInMemoryDatabase($"audit-null-{Guid.NewGuid():N}").Options);

        Assert.Throws<ArgumentNullException>(() => db.ChangeTracker.OnEntityEnteringAdded(null!));
    }
}
