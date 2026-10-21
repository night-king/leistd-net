using Leistd.Auditing.Abstractions;
using Leistd.Authorization.Grants;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.EntityFrameworkCore.Entities;

/// <summary>持久化的权限授予记录；一行即一次授予，同一主体对同一权限只有一行。</summary>
/// <remarks>
/// 授予按租户分区。写入须经 <see cref="IPermissionGrantManager"/>，直接写库会绕过归一化。
/// </remarks>
public class PermissionGrantRecord : ICreationAuditedObject, IMultiTenant
{
    /// <summary>权限授予 ID（有序 Guid v7）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid? TenantId { get; set; }

    /// <summary>权限名称。</summary>
    public string PermissionName { get; set; } = default!;

    /// <summary>授予对象类型，如 User、Role。</summary>
    public string ProviderName { get; set; } = default!;

    /// <summary>授予对象 Key，如 UserId、RoleId。</summary>
    public string ProviderKey { get; set; } = default!;

    /// <inheritdoc />
    public DateTime CreationTime { get; set; }

    /// <inheritdoc />
    public string? CreatorId { get; set; }
}
