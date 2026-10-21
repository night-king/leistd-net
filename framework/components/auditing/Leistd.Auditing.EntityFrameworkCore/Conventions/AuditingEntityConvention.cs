using Leistd.Auditing.Abstractions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Leistd.Auditing.EntityFrameworkCore.Conventions;

/// <summary>将实体审计人字段的默认长度配置为 64。</summary>
/// <remarks>在 ConfigureConventions 中显式登记；Fluent 配置与数据注解优先。</remarks>
public sealed class AuditingEntityConvention : IModelFinalizingConvention
{
    private const int AuditUserIdMaxLength = 64;

    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (typeof(ICreationAuditedObject).IsAssignableFrom(clrType))
                entityType.FindProperty(nameof(ICreationAuditedObject.CreatorId))?.Builder.HasMaxLength(AuditUserIdMaxLength);
            if (typeof(IModificationAuditedObject).IsAssignableFrom(clrType))
                entityType.FindProperty(nameof(IModificationAuditedObject.LastModifierId))?.Builder.HasMaxLength(AuditUserIdMaxLength);
            if (typeof(IDeletionAuditedObject).IsAssignableFrom(clrType))
                entityType.FindProperty(nameof(IDeletionAuditedObject.DeleterId))?.Builder.HasMaxLength(AuditUserIdMaxLength);
        }
    }
}
