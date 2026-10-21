using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Leistd.Authorization.Resource.EntityFrameworkCore.Entities;
using Leistd.Authorization.Resource.Grants;

namespace Leistd.Authorization.Resource.EntityFrameworkCore.EntityConfigurations;

/// <summary><see cref="ResourcePermissionGrantRecord"/> 的实体配置。</summary>
public class ResourcePermissionGrantRecordConfiguration
    : IEntityTypeConfiguration<ResourcePermissionGrantRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ResourcePermissionGrantRecord> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ResourceName)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.ResourceKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Operation)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.ProviderName)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.ProviderKey)
            .HasMaxLength(128)
            .IsRequired();

        // 以字符串存储：可读、可演进，且枚举成员重排序不会让既有数据错位。
        builder.Property(x => x.Effect)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // 字符串列挡不住越界值（999 会存成 "999"），检查约束让绕过管理器的写入同样受限
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_ResourcePermissionGrants_Effect",
            $"\"{nameof(ResourcePermissionGrantRecord.Effect)}\" IN ('{nameof(ResourceGrantEffect.Granted)}', '{nameof(ResourceGrantEffect.Prohibited)}')"));

        builder.Property(x => x.CreatorId)
            .HasMaxLength(64);

        // ACL 随资源按租户分区。宿主行与租户行分别用带过滤的唯一索引：
        // 可空 TenantId 直接进唯一索引时 NULL 互不相等，宿主行失去唯一性兜底
        builder.HasIndex(x => new
            {
                x.ResourceName,
                x.ResourceKey,
                x.Operation,
                x.ProviderName,
                x.ProviderKey
            })
            .IsUnique()
            .HasFilter($"\"{nameof(ResourcePermissionGrantRecord.TenantId)}\" IS NULL");

        builder.HasIndex(x => new
            {
                x.TenantId,
                x.ResourceName,
                x.ResourceKey,
                x.Operation,
                x.ProviderName,
                x.ProviderKey
            })
            .IsUnique()
            .HasFilter($"\"{nameof(ResourcePermissionGrantRecord.TenantId)}\" IS NOT NULL");

        // 集合级查询入口：按资源类型 + 操作 + 主体过滤，支撑合并进业务查询的 IN/EXISTS。
        builder.HasIndex(x => new { x.TenantId, x.ResourceName, x.Operation, x.ProviderName, x.ProviderKey });

        // 资源删除后的清理入口。
        builder.HasIndex(x => new { x.ResourceName, x.ResourceKey });
    }
}
