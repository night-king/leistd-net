using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.Auditing.Abstractions;

namespace Leistd.MultiTenancy.EntityFrameworkCore.Entities;

/// <summary>表示宿主控制库中租户的一条连接登记。</summary>
/// <remarks>
/// 这一行决定该租户在这个服务的数据落在哪个库，因此除并发令牌外还带审计字段。
/// 主键是 <see cref="TenantId"/> + <see cref="Name"/>；一条都没有即该租户不单独分库。
/// 不实现 <see cref="ISoftDelete"/>：随 <see cref="TenantRecord"/> 级联删除。
/// 写入统一通过 <see cref="ITenantConnectionConfigurationManager"/>，它负责名字归一化、加密、版本递增与时间填充。
/// </remarks>
public class TenantConnectionRecord : ICreationAuditedObject, IModificationAuditedObject
{
    /// <summary>租户 Id，主键的一部分，同时是 <see cref="TenantRecord"/> 外键。</summary>
    public Guid TenantId { get; set; }

    /// <summary>归一化（小写）后的连接名，主键的一部分。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>连接串的 Data Protection 密文。</summary>
    /// <remarks>只能经管理器写入（它负责加密）；直接写明文会在读取时因解密失败被拒绝。</remarks>
    public string ProtectedConnectionString { get; set; } = string.Empty;

    /// <summary>该行的配置版本。同时作为并发令牌，防止并发写入互相覆盖。</summary>
    public long Version { get; set; }

    /// <inheritdoc />
    public DateTime CreationTime { get; set; }

    /// <inheritdoc />
    public string? CreatorId { get; set; }

    /// <inheritdoc />
    public DateTime? LastModificationTime { get; set; }

    /// <inheritdoc />
    public string? LastModifierId { get; set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{nameof(TenantConnectionRecord)} {{ TenantId = {TenantId}, Name = {Name}, Version = {Version} }}";
}
