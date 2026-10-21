using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Leistd.OperationRecords.EntityFrameworkCore.EntityConfigurations;

/// <summary><see cref="OperationRecordArchive"/> 的实体配置。</summary>
/// <remarks>列长度引用 <see cref="OperationRecordInfo"/> 的上限常量，与原表同源，搬运时不截断。</remarks>
public class OperationRecordArchiveConfiguration : IEntityTypeConfiguration<OperationRecordArchive>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OperationRecordArchive> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action).HasMaxLength(OperationRecordInfo.MaxActionLength).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(OperationRecordInfo.MaxTargetIdLength).IsRequired();
        builder.Property(x => x.AuthorizationBasis).HasMaxLength(OperationRecordInfo.MaxAuthorizationBasisLength).IsRequired();

        // 与原表同样存字符串：便于直接查询，且枚举重排不改变历史含义
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        // 库默认值用于给存量表加列时回填，否则历史行读取时枚举转换失败
        builder.Property(x => x.Visibility)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(OperationVisibility.Host)
            // 必须配 ValueGeneratedNever：否则属性等于 CLR 默认值（Tenant = 0）时 EF 省略该列，
            // 租户可见的记录会被库默认值写成宿主可见
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.ActorId).HasMaxLength(OperationRecordInfo.MaxActorIdLength);
        builder.Property(x => x.ActorName).HasMaxLength(OperationRecordInfo.MaxActorNameLength);
        builder.Property(x => x.ImpersonatorId).HasMaxLength(OperationRecordInfo.MaxActorIdLength);
        builder.Property(x => x.ImpersonatorName).HasMaxLength(OperationRecordInfo.MaxActorNameLength);
        builder.Property(x => x.CorrelationId).HasMaxLength(OperationRecordInfo.MaxCorrelationIdLength);
        builder.Property(x => x.TargetName).HasMaxLength(OperationRecordInfo.MaxTargetNameLength);
        builder.Property(x => x.FailureCode).HasMaxLength(OperationRecordInfo.MaxFailureCodeLength);
        builder.Property(x => x.FailureDetail).HasMaxLength(OperationRecordInfo.MaxFailureDetailLength);

        // 归档按原始发生时间检索，索引不用归档时刻
        builder.HasIndex(x => new { x.TenantId, x.CreationTime }).IsDescending(false, true);
    }
}
