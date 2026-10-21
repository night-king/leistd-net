using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Leistd.Authorization.EntityFrameworkCore.Entities;

namespace Leistd.Authorization.EntityFrameworkCore.EntityConfigurations;

/// <summary><see cref="PermissionGrantRecord"/> 的实体配置。</summary>
public class PermissionGrantRecordConfiguration : IEntityTypeConfiguration<PermissionGrantRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermissionGrantRecord> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PermissionName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.ProviderName)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.ProviderKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.CreatorId)
            .HasMaxLength(64);

        // 可空 TenantId 的唯一索引不能约束宿主重复行，故宿主与租户分别使用过滤唯一索引。
        builder.HasIndex(x => new { x.PermissionName, x.ProviderName, x.ProviderKey })
            .IsUnique()
            .HasFilter($"\"{nameof(PermissionGrantRecord.TenantId)}\" IS NULL");

        builder.HasIndex(x => new { x.TenantId, x.PermissionName, x.ProviderName, x.ProviderKey })
            .IsUnique()
            .HasFilter($"\"{nameof(PermissionGrantRecord.TenantId)}\" IS NOT NULL");

        builder.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey });
    }
}

/// <summary><see cref="AuthorizationVersionRecord"/> 的实体配置。</summary>
public class AuthorizationVersionRecordConfiguration : IEntityTypeConfiguration<AuthorizationVersionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuthorizationVersionRecord> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderName)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.ProviderKey)
            .HasMaxLength(128)
            .IsRequired();

        // 并发令牌：UPDATE 带 WHERE Version = @original，由数据库收口读-比-写窗口
        builder.Property(x => x.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(x => x.LastModifierId)
            .HasMaxLength(64);

        // 版本随授予按租户分区；宿主行与租户行分别用带过滤的唯一索引（NULL 唯一性说明同上）。
        // 并发首写的兜底依赖这两个索引：两个事务同时为同一主体插入版本行时，落败方由数据库拒绝
        builder.HasIndex(x => new { x.ProviderName, x.ProviderKey })
            .IsUnique()
            .HasFilter($"\"{nameof(AuthorizationVersionRecord.TenantId)}\" IS NULL");

        builder.HasIndex(x => new { x.TenantId, x.ProviderName, x.ProviderKey })
            .IsUnique()
            .HasFilter($"\"{nameof(AuthorizationVersionRecord.TenantId)}\" IS NOT NULL");
    }
}
