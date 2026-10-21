using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Leistd.OperationRecords.EntityFrameworkCore.EntityConfigurations;

/// <summary><see cref="OperationRecord"/> 的实体配置。</summary>
public class OperationRecordConfiguration : IEntityTypeConfiguration<OperationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OperationRecord> builder)
    {
        // 表名沿用 EF Core 默认约定，不加框架前缀
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action)
            .HasMaxLength(OperationRecordInfo.MaxActionLength)
            .IsRequired();

        builder.Property(x => x.TargetId)
            .HasMaxLength(OperationRecordInfo.MaxTargetIdLength)
            .IsRequired();

        builder.Property(x => x.AuthorizationBasis)
            .HasMaxLength(OperationRecordInfo.MaxAuthorizationBasisLength)
            .IsRequired();

        // 存字符串而不是序号：便于直接查询，且枚举重排不改变历史含义；显式配置优先于宿主的全局 Enum 约定
        builder.Property(x => x.Outcome)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // 同 Outcome 存字符串。库默认值用于给存量表加列时回填，否则历史行读取时枚举转换失败
        builder.Property(x => x.Visibility)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(OperationVisibility.Host)
            // 必须配 ValueGeneratedNever：否则属性等于 CLR 默认值（Tenant = 0）时 EF 省略该列，
            // 租户可见的记录会被库默认值写成宿主可见
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.ActorId).HasMaxLength(OperationRecord.MaxActorIdLength);
        builder.Property(x => x.ActorName).HasMaxLength(OperationRecordInfo.MaxActorNameLength);
        builder.Property(x => x.ImpersonatorId).HasMaxLength(OperationRecord.MaxActorIdLength);
        builder.Property(x => x.ImpersonatorName).HasMaxLength(OperationRecordInfo.MaxActorNameLength);
        builder.Property(x => x.CorrelationId).HasMaxLength(OperationRecordInfo.MaxCorrelationIdLength);
        builder.Property(x => x.TargetName).HasMaxLength(OperationRecordInfo.MaxTargetNameLength);
        builder.Property(x => x.FailureCode).HasMaxLength(OperationRecordInfo.MaxFailureCodeLength);
        builder.Property(x => x.FailureDetail).HasMaxLength(OperationRecordInfo.MaxFailureDetailLength);
        // FailureData 不设长度上限：截断的 JSON 无法解析

        // 查询形态是“某租户最近若干条”，索引按 (租户, 时间倒序)；关键字在其结果上过滤，不单独建索引（写多读少）
        builder.HasIndex(x => new { x.TenantId, x.CreationTime }).IsDescending(false, true);

        // 非宿主读者的查询都带可见性过滤
        builder.HasIndex(x => new { x.TenantId, x.Visibility, x.CreationTime }).IsDescending(false, false, true);

        // 保留期归档整库按时间升序扫（IgnoreQueryFilters，不带 TenantId），上面两条以 TenantId 打头的索引用不上
        builder.HasIndex(x => x.CreationTime);
    }
}
