using Leistd.Auditing.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Leistd.Auditing.Tests;

/// <summary>审计组件的被测夹具：一个不继承 DDD 基座的普通 <see cref="DbContext"/>。</summary>
/// <remarks>
/// 刻意不用 <c>BaseDbContext</c>：审计要能独立于 DDD 基座工作（迁移作业、宿主控制面上下文
/// 都是普通 DbContext）。用基座测会把两件事的失败混在一起，也测不出这条独立性。
/// DDD 组合下的创建审计时序另有 <c>Leistd.Ddd.Infrastructure.Tests</c> 覆盖。
/// </remarks>
public sealed class AuditedEntity : IFullAuditedObject
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";

    public DateTime CreationTime { get; set; }
    public string? CreatorId { get; set; }
    public DateTime? LastModificationTime { get; set; }
    public string? LastModifierId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletionTime { get; set; }
    public string? DeleterId { get; set; }
}

/// <summary>只实现创建审计的实体：用来钉"按接口逐项判定"，而不是一刀切全写。</summary>
public sealed class CreationOnlyEntity : IHasCreationTime
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTime CreationTime { get; set; }
}

/// <summary>完全不实现任何审计接口：拦截器必须原样放过。</summary>
public sealed class PlainEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
}

/// <summary>普通上下文，不继承 DDD 基座。</summary>
public sealed class AuditTestDbContext(DbContextOptions<AuditTestDbContext> options) : DbContext(options)
{
    public DbSet<AuditedEntity> Audited => Set<AuditedEntity>();
    public DbSet<CreationOnlyEntity> CreationOnly => Set<CreationOnlyEntity>();
    public DbSet<PlainEntity> Plain => Set<PlainEntity>();
}
