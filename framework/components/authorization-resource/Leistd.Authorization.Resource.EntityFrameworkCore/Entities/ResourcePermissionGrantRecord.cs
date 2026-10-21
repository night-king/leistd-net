using Leistd.Authorization.Resource.Grants;
using Leistd.Auditing.Abstractions;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.Resource.EntityFrameworkCore.Entities;

/// <summary>资源实例 ACL 持久化实体。</summary>
/// <remarks>
/// 对业务表没有外键，资源删除后需显式清理并安排周期性孤儿检查。ACL 随资源按租户分区。
/// </remarks>
public class ResourcePermissionGrantRecord : ICreationAuditedObject, IMultiTenant
{
    /// <summary>ACL 记录 ID（有序 Guid v7）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属租户 Id，<see langword="null"/> 为宿主资源的 ACL。</summary>
    public Guid? TenantId { get; set; }

    /// <summary>资源类型名称。</summary>
    public string ResourceName { get; set; } = default!;

    /// <summary>资源实例 Key。</summary>
    public string ResourceKey { get; set; } = default!;

    /// <summary>被授予的操作。</summary>
    public string Operation { get; set; } = default!;

    /// <summary>授予对象类型，如 User、Role。</summary>
    public string ProviderName { get; set; } = default!;

    /// <summary>授予对象 Key。</summary>
    public string ProviderKey { get; set; } = default!;

    /// <summary>授予效果：显式允许或显式拒绝。</summary>
    public ResourceGrantEffect Effect { get; set; } = ResourceGrantEffect.Granted;

    /// <inheritdoc />
    public DateTime CreationTime { get; set; }

    /// <inheritdoc />
    public string? CreatorId { get; set; }
}
