using Leistd.Ddd.Domain.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Leistd.Ddd.Infrastructure.Persistence.Conventions;

/// <summary>将实体并发标记配置为必填、最长 40 的并发令牌。</summary>
/// <remarks>以约定来源配置，Fluent 配置与数据注解优先；BaseDbContext 自动登记此约定。</remarks>
public sealed class DddEntityConvention : IModelFinalizingConvention
{
    private const int ConcurrencyStampMaxLength = 40;

    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(IHasConcurrencyStamp).IsAssignableFrom(clrType)
                && entityType.FindProperty(nameof(IHasConcurrencyStamp.ConcurrencyStamp)) is { } stamp)
            {
                // 三项各自以约定来源写入：某一项被显式配置覆盖时，其余两项照常生效。
                stamp.Builder.HasMaxLength(ConcurrencyStampMaxLength);
                stamp.Builder.IsRequired(true);
                stamp.Builder.IsConcurrencyToken(true);
            }
        }
    }
}
