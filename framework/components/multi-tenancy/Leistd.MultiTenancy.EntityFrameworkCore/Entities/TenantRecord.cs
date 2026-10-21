using Leistd.MultiTenancy.Stores;
using Leistd.Auditing.Abstractions;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Entities;

/// <summary>表示宿主侧的持久化租户记录。</summary>
/// <remarks>
/// 写入请统一通过 <see cref="ITenantManager"/>：它负责名称归一化与未删除行内的唯一性校验。
/// 直接写库不会留下陈旧读取（存储直接读库），但会绕开这两项校验。
/// </remarks>
public class TenantRecord : IFullAuditedObject
{
    /// <summary>租户标识。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>大小写不敏感的唯一租户名称。</summary>
    public string Name { get; set; } = default!;

    /// <summary>归一化查询键。</summary>
    public string NormalizedName { get; set; } = default!;

    /// <summary>显示名称。</summary>
    public string? DisplayName { get; set; }

    /// <summary>简短描述。</summary>
    public string? Description { get; set; }

    /// <summary>租户是否启用。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>租户生命周期的并发版本，同时作为 EF 并发令牌。</summary>
    /// <remarks>
    /// 启用、停用、改名与连接配置的创建/修改全部竞争这一个版本，因此同一租户的这些管理操作互斥，
    /// 并发时落败方抛 <c>TenantConcurrencyConflictException</c>（409），使“改路由前必须已停用”成为数据库层面的不变量。
    /// </remarks>
    public long Version { get; set; } = 1;

    /// <inheritdoc />
    public DateTime CreationTime { get; set; }

    /// <inheritdoc />
    public string? CreatorId { get; set; }

    /// <inheritdoc />
    public DateTime? LastModificationTime { get; set; }

    /// <inheritdoc />
    public string? LastModifierId { get; set; }

    /// <inheritdoc />
    public bool IsDeleted { get; set; }

    /// <inheritdoc />
    public DateTime? DeletionTime { get; set; }

    /// <inheritdoc />
    public string? DeleterId { get; set; }
}
