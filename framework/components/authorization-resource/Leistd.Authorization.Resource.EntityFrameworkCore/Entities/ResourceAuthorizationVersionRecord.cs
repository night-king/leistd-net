using Leistd.Auditing.Abstractions;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.Resource.EntityFrameworkCore.Entities;

/// <summary>资源实例的 ACL 版本，每次写入递增，用于全量替换的乐观并发。</summary>
public class ResourceAuthorizationVersionRecord : IModificationAuditedObject, IMultiTenant
{
    /// <summary>版本记录 ID（有序 Guid v7）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属租户 Id，<see langword="null"/> 为宿主；版本随 ACL 按租户分区。</summary>
    public Guid? TenantId { get; set; }

    /// <summary>资源类型名。</summary>
    public string ResourceName { get; set; } = default!;

    /// <summary>资源实例 Key。</summary>
    public string ResourceKey { get; set; } = default!;

    /// <summary>当前版本号，从 1 开始，每次 ACL 写入递增。</summary>
    public long Version { get; set; }

    /// <inheritdoc />
    public DateTime? LastModificationTime { get; set; }

    /// <inheritdoc />
    public string? LastModifierId { get; set; }
}
