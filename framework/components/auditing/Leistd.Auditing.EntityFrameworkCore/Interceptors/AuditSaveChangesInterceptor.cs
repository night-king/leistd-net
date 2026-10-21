using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Leistd.Auditing.Abstractions;

namespace Leistd.Auditing.EntityFrameworkCore.Interceptors;

/// <summary>在保存时填充修改与删除审计，并将软删除转为更新。</summary>
/// <remarks>创建审计在实体进入跟踪时落定，本拦截器不处理新增实体。</remarks>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IAuditPropertySetter _auditPropertySetter;

    /// <summary>创建拦截器。</summary>
    public AuditSaveChangesInterceptor(IAuditPropertySetter auditPropertySetter)
    {
        _auditPropertySetter = auditPropertySetter;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        UpdateAuditFields(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        UpdateAuditFields(eventData.Context);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    private void UpdateAuditFields(DbContext? context)
    {
        if (context == null)
            return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Modified:
                    // 直接设置 IsDeleted 会保持 Modified 状态，仍需记录删除审计。
                    if (entry.Entity is ISoftDelete { IsDeleted: true })
                    {
                        if (IsSoftDeleteTransition(entry))
                        {
                            _auditPropertySetter.SetDeletionProperties(entry);
                        }

                        // 删除审计取代同次保存的修改审计。
                        break;
                    }

                    _auditPropertySetter.SetModificationProperties(entry);
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is ISoftDelete)
                    {
                        entry.State = EntityState.Modified;
                        _auditPropertySetter.SetDeletionProperties(entry);
                    }
                    break;
            }
        }
    }

    // 只识别本次 false 到 true 的迁移，避免后续编辑覆盖未知的删除者。
    private static bool IsSoftDeleteTransition(EntityEntry entry)
    {
        var property = entry.Property(nameof(ISoftDelete.IsDeleted));

        return property.OriginalValue is false;
    }
}
