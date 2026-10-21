using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Leistd.Settings.EntityFrameworkCore.Entities;

namespace Leistd.Settings.EntityFrameworkCore.EntityConfigurations;

/// <summary><see cref="SettingRecord"/> 的实体配置。</summary>
public class SettingRecordConfiguration : IEntityTypeConfiguration<SettingRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SettingRecord> builder)
    {
        // 表名沿用 EF Core 默认约定，不加框架前缀
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).HasMaxLength(128);

        builder.Property(x => x.ScopeKey)
            .HasMaxLength(192)
            .IsRequired();

        builder.Property(x => x.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.Value)
            .HasMaxLength(2000)
            .IsRequired();

        // 用非空 ScopeKey 约束唯一性：可空的 TenantId/UserId 进不了有效的唯一约束
        builder.HasIndex(x => new { x.ScopeKey, x.Name }).IsUnique();
    }
}
