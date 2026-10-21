using Leistd.BackgroundJobs.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Leistd.BackgroundJobs.EntityFrameworkCore.EntityConfigurations;

/// <summary><see cref="RecurringJobState"/> 的实体配置。</summary>
public class RecurringJobStateConfiguration : IEntityTypeConfiguration<RecurringJobState>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RecurringJobState> builder)
    {
        // 表名沿用 EF Core 默认约定，不加框架前缀
        builder.HasKey(x => x.Name);
        builder.Property(x => x.Name).HasMaxLength(RecurringJobState.MaxNameLength);
    }
}
