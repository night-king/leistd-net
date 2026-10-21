using Leistd.Auditing.Abstractions;
using Leistd.Auditing.EntityFrameworkCore.Services;
using Leistd.TestBase.Doubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.Auditing.Tests;

/// <summary>审计字段的落值规则：谁写、写什么、什么时候不写。</summary>
public class AuditPropertySetterTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly AuditTestDbContext _db = new(
        new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseInMemoryDatabase($"audit-{Guid.NewGuid():N}")
            .Options);

    public void Dispose() => _db.Dispose();

    private IAuditPropertySetter Setter(Guid? userId = null) =>
        new AuditPropertySetter(new FakeClock(Now), userId is null ? null : new FakeCurrentUser(userId));

    [Fact]
    public void Creation_sets_time_and_creator()
    {
        var entity = new AuditedEntity();
        var entry = _db.Attach(entity);

        Setter(UserId).SetCreationProperties(entry);

        Assert.Equal(Now, entity.CreationTime);
        Assert.Equal(UserId.ToString(), entity.CreatorId);
    }

    // 已有值不覆盖：导入历史数据、领域层自己设过时间的场景都靠这条。
    [Fact]
    public void Creation_never_overwrites_values_already_set()
    {
        var original = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new AuditedEntity { CreationTime = original, CreatorId = "importer" };

        Setter(UserId).SetCreationProperties(_db.Attach(entity));

        Assert.Equal(original, entity.CreationTime);
        Assert.Equal("importer", entity.CreatorId);
    }

    // ICurrentUser 是可选依赖：没有身份栈时时间照常落值，只有用户字段留空。
    // 迁移作业与后台任务全靠这条，否则它们要为了写审计引入整个 ASP.NET Core 身份栈。
    [Fact]
    public void Anonymous_context_still_records_time_but_leaves_the_user_empty()
    {
        var entity = new AuditedEntity();

        Setter(userId: null).SetCreationProperties(_db.Attach(entity));

        Assert.Equal(Now, entity.CreationTime);
        Assert.Null(entity.CreatorId);
    }

    // 已登录但 Id 为空（例如只有客户端凭据的机器主体）同样按匿名处理。
    [Fact]
    public void Current_user_without_id_is_treated_as_anonymous()
    {
        var entity = new AuditedEntity();
        var setter = new AuditPropertySetter(new FakeClock(Now), new FakeCurrentUser(id: null, username: "svc"));

        setter.SetCreationProperties(_db.Attach(entity));

        Assert.Equal(Now, entity.CreationTime);
        Assert.Null(entity.CreatorId);
    }

    // 修改时间与创建时间相反：每次都覆盖，"最后一次"才是它的语义。
    [Fact]
    public void Modification_always_overwrites_the_previous_value()
    {
        var entity = new AuditedEntity
        {
            LastModificationTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastModifierId = "someone-else",
        };

        Setter(UserId).SetModificationProperties(_db.Attach(entity));

        Assert.Equal(Now, entity.LastModificationTime);
        Assert.Equal(UserId.ToString(), entity.LastModifierId);
    }

    // 后台作业或机器主体做的修改：修改人必须清空，否则"新时间 + 上一个人"会把这次修改算到别人头上
    [Fact]
    public void Anonymous_modification_clears_the_previous_modifier()
    {
        var entity = new AuditedEntity { LastModifierId = "someone-else" };

        Setter(userId: null).SetModificationProperties(_db.Attach(entity));

        Assert.Equal(Now, entity.LastModificationTime);
        Assert.Null(entity.LastModifierId);
    }

    [Fact]
    public void Deletion_marks_the_flag_and_records_who_and_when()
    {
        var entity = new AuditedEntity();

        Setter(UserId).SetDeletionProperties(_db.Attach(entity));

        Assert.True(entity.IsDeleted);
        Assert.Equal(Now, entity.DeletionTime);
        Assert.Equal(UserId.ToString(), entity.DeleterId);
    }

    // 二次删除不得改写首次的删除者与时间——那会抹掉"谁删的"这个唯一证据。
    [Fact]
    public void Deleting_twice_keeps_the_first_deleter_and_timestamp()
    {
        var first = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = new AuditedEntity { IsDeleted = true, DeletionTime = first, DeleterId = "first" };

        Setter(UserId).SetDeletionProperties(_db.Attach(entity));

        Assert.Equal(first, entity.DeletionTime);
        Assert.Equal("first", entity.DeleterId);
    }

    // 只实现部分审计接口的实体只写它声明的那部分，不能因为缺属性而抛。
    [Fact]
    public void Entities_implementing_only_part_of_the_contract_get_only_that_part()
    {
        var entity = new CreationOnlyEntity();
        var entry = _db.Attach(entity);
        var setter = Setter(UserId);

        setter.SetCreationProperties(entry);
        setter.SetModificationProperties(entry);
        setter.SetDeletionProperties(entry);

        Assert.Equal(Now, entity.CreationTime);
    }

    [Fact]
    public void Entities_without_any_audit_contract_are_left_alone()
    {
        var entity = new PlainEntity { Name = "x" };
        var entry = _db.Attach(entity);
        var setter = Setter(UserId);

        setter.SetCreationProperties(entry);
        setter.SetModificationProperties(entry);
        setter.SetDeletionProperties(entry);

        Assert.Equal("x", entity.Name);
    }

    // 接口用 object 是为了让 Core 包不依赖 EF Core，不是允许传实体本身。
    // 传错类型必须立刻失败——静默返回会让审计字段永远为空且无人察觉。
    [Fact]
    public void Passing_a_detached_entity_instead_of_an_entry_fails_loudly()
    {
        var setter = Setter(UserId);

        var ex = Assert.Throws<ArgumentException>(() => setter.SetCreationProperties(new AuditedEntity()));
        Assert.Contains("EntityEntry", ex.Message);

        Assert.Throws<ArgumentException>(() => setter.SetModificationProperties(new AuditedEntity()));
        Assert.Throws<ArgumentException>(() => setter.SetDeletionProperties(new AuditedEntity()));
    }

    // 时钟给出非 UTC 值时必须先归一化：审计时间线是跨服务比较的，混入本地时间会静默错位。
    [Fact]
    public void Times_are_normalized_to_utc_before_being_written()
    {
        var local = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local);
        var entity = new AuditedEntity();

        new AuditPropertySetter(new FakeClock(local), null).SetCreationProperties(_db.Attach(entity));

        Assert.Equal(DateTimeKind.Utc, entity.CreationTime.Kind);
        Assert.Equal(local.ToUniversalTime(), entity.CreationTime);
    }
}
