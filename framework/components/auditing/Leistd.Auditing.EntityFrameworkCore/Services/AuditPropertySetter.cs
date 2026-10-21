using Leistd.Security.Users;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Leistd.Timing;
using Leistd.Auditing.Abstractions;

namespace Leistd.Auditing.EntityFrameworkCore.Services;

/// <summary>使用 EF Core 变更跟踪器填充审计属性。</summary>
/// <remarks>
/// 只处理时间与用户审计，不处理租户归属。<see cref="ICurrentUser"/> 可选：未注册时时间审计照常落值，用户字段保持 <see langword="null"/>。
/// </remarks>
public class AuditPropertySetter(
    IClock clock,
    ICurrentUser? currentUser) : IAuditPropertySetter
{
    /// <inheritdoc/>
    public virtual void SetCreationProperties(object entityEntry)
    {
        var entry = AsEntry(entityEntry);

        SetCreationTime(entry);
        SetCreatorId(entry);
    }

    /// <inheritdoc/>
    public virtual void SetModificationProperties(object entityEntry)
    {
        var entry = AsEntry(entityEntry);

        SetLastModificationTime(entry);
        SetLastModifierId(entry);
    }

    /// <inheritdoc/>
    public virtual void SetDeletionProperties(object entityEntry)
    {
        var entry = AsEntry(entityEntry);

        SetIsDeleted(entry);
        SetDeletionTime(entry);
        SetDeleterId(entry);
    }

    // 核心接口用 object 隔离 EF Core 依赖；适配层对错误类型失败关闭。
    private static EntityEntry AsEntry(object entityEntry)
    {
        if (entityEntry is EntityEntry entry)
        {
            return entry;
        }

        throw new ArgumentException(
            $"Expected an {nameof(EntityEntry)} but received '{entityEntry?.GetType().FullName ?? "null"}'. " +
            $"Audit properties are set through the EF Core change tracker, not on detached entities.",
            nameof(entityEntry));
    }

    /// <summary>设置尚未赋值的创建时间。</summary>
    protected virtual void SetCreationTime(EntityEntry entry)
    {
        if (entry.Entity is not IHasCreationTime objectWithCreationTime)
            return;

        if (objectWithCreationTime.CreationTime != default)
            return;

        entry.Property(nameof(IHasCreationTime.CreationTime)).CurrentValue = clock.Normalize(clock.Now);
    }

    /// <summary>设置尚未赋值的创建者标识。</summary>
    protected virtual void SetCreatorId(EntityEntry entry)
    {
        if (currentUser?.Id is null)
            return;

        if (entry.Entity is not ICreationAuditedObject creationAuditedObject)
            return;

        if (!string.IsNullOrEmpty(creationAuditedObject.CreatorId))
            return;

        entry.Property(nameof(ICreationAuditedObject.CreatorId)).CurrentValue = currentUser!.Id!.Value.ToString();
    }

    /// <summary>设置最后修改时间。</summary>
    protected virtual void SetLastModificationTime(EntityEntry entry)
    {
        if (entry.Entity is not IHasModificationTime)
            return;

        entry.Property(nameof(IHasModificationTime.LastModificationTime)).CurrentValue = clock.Normalize(clock.Now);
    }

    /// <summary>设置最后修改者标识；没有当前用户时置空，与同时刷新的修改时间保持同一次修改。</summary>
    protected virtual void SetLastModifierId(EntityEntry entry)
    {
        if (entry.Entity is not IModificationAuditedObject)
            return;

        entry.Property(nameof(IModificationAuditedObject.LastModifierId)).CurrentValue = currentUser?.Id?.ToString();
    }

    /// <summary>设置软删除标记。</summary>
    protected virtual void SetIsDeleted(EntityEntry entry)
    {
        if (entry.Entity is not ISoftDelete softDelete)
            return;

        if (softDelete.IsDeleted)
            return;

        entry.Property(nameof(ISoftDelete.IsDeleted)).CurrentValue = true;
    }

    /// <summary>设置尚未赋值的删除时间。</summary>
    protected virtual void SetDeletionTime(EntityEntry entry)
    {
        if (entry.Entity is not IHasDeletionTime objectWithDeletionTime)
            return;

        if (objectWithDeletionTime.DeletionTime.HasValue)
            return;

        entry.Property(nameof(IHasDeletionTime.DeletionTime)).CurrentValue = clock.Normalize(clock.Now);
    }

    /// <summary>设置尚未赋值的删除者标识。</summary>
    protected virtual void SetDeleterId(EntityEntry entry)
    {
        if (currentUser?.Id is null)
            return;

        if (entry.Entity is not IDeletionAuditedObject deletionAuditedObject)
            return;

        if (!string.IsNullOrEmpty(deletionAuditedObject.DeleterId))
            return;

        entry.Property(nameof(IDeletionAuditedObject.DeleterId)).CurrentValue = currentUser!.Id!.Value.ToString();
    }
}
