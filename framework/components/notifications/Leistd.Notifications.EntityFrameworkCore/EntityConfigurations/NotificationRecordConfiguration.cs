using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Leistd.Notifications.EntityFrameworkCore.Entities;

namespace Leistd.Notifications.EntityFrameworkCore.EntityConfigurations;

/// <summary><see cref="NotificationRecord"/> 的实体配置。</summary>
public class NotificationRecordConfiguration : IEntityTypeConfiguration<NotificationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationRecord> builder)
    {
        // 表名沿用 EF Core 默认约定，不加框架前缀
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Title)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.Content)
            .HasMaxLength(2000);

        builder.Property(x => x.Type)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Link)
            .HasMaxLength(512);

        builder.Property(x => x.Icon)
            .HasMaxLength(64);

        builder.Property(x => x.RelatedEntityId)
            .HasMaxLength(128);

        builder.Property(x => x.RelatedEntityType)
            .HasMaxLength(64);

        builder.Property(x => x.CreatorId)
            .HasMaxLength(64);

        // 用户通知列表
        builder.HasIndex(x => new { x.UserId, x.CreationTime });

        // 未读数
        builder.HasIndex(x => new { x.UserId, x.IsRead });

        // 保留期清理整库按时间扫（IgnoreQueryFilters，不带 UserId）；清理谓词的两个分支都受较宽的时间上界约束，单列索引即可
        builder.HasIndex(x => x.CreationTime);
    }
}
