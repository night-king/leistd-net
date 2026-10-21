using Leistd.Authorization.Grants;
using Leistd.Auditing.Abstractions;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.EntityFrameworkCore.Entities;

/// <summary>授予主体的授权版本，每次写入递增。</summary>
/// <remarks>
/// 用于批量替换的乐观并发与客户端缓存过期判断（见 <see cref="SubjectPermissionGrants.VersionToken"/>）。
/// </remarks>
public class AuthorizationVersionRecord : IModificationAuditedObject, IMultiTenant
{
    /// <summary>版本记录 ID（有序 Guid v7）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属租户 Id，<see langword="null"/> 为宿主；版本随授予按租户分区。</summary>
    public Guid? TenantId { get; set; }

    /// <summary>授予对象类型，如 User、Role。</summary>
    public string ProviderName { get; set; } = default!;

    /// <summary>授予对象 Key，如 UserId、RoleId。</summary>
    public string ProviderKey { get; set; } = default!;

    /// <summary>当前版本号，从 1 开始，每次授权写入递增。</summary>
    public long Version { get; set; }

    /// <inheritdoc />
    public DateTime? LastModificationTime { get; set; }

    /// <inheritdoc />
    public string? LastModifierId { get; set; }
}
