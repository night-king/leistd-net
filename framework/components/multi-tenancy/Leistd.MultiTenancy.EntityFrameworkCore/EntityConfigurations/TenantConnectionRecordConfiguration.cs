using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.ConnectionStrings;

namespace Leistd.MultiTenancy.EntityFrameworkCore.EntityConfigurations;

/// <summary>租户连接登记的 EF Core 配置。</summary>
/// <remarks>主键是 <c>(TenantId, Name)</c>，一个租户可登记多条；密文列非空。</remarks>
public class TenantConnectionRecordConfiguration : IEntityTypeConfiguration<TenantConnectionRecord>
{
    /// <summary>密文列的最大长度：明文上限经 Data Protection 加密并 Base64Url 编码后的余量。</summary>
    public const int MaxProtectedConnectionStringLength = 4096;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantConnectionRecord> builder)
    {
        builder.HasKey(x => new { x.TenantId, x.Name });

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(TenantConnectionConfiguration.MaxNameLength);

        // 密文不进索引：它是配置值不是标识符，进索引只会多留副本
        builder.Property(x => x.ProtectedConnectionString)
            .IsRequired()
            .HasMaxLength(MaxProtectedConnectionStringLength);

        builder.Property(x => x.Version).IsRequired().IsConcurrencyToken();

        builder.Property(x => x.CreatorId).HasMaxLength(64);
        builder.Property(x => x.LastModifierId).HasMaxLength(64);

        // 一对多：同一租户可以在不同服务各登记一条
        builder.HasOne<TenantRecord>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
