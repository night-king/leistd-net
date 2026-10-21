namespace Leistd.Auditing.Abstractions;

/// <summary>通过持久化适配层填充实体审计属性。</summary>
/// <remarks>
/// 参数为 <c>object</c> 只为不依赖 EF Core：EF Core 适配层要求传入 <c>EntityEntry</c>，传实体本身或其他类型抛 <see cref="ArgumentException"/>。
/// </remarks>
public interface IAuditPropertySetter
{
    /// <summary>填充未设置的创建审计属性。</summary>
    /// <param name="entityEntry">实体变更追踪条目。</param>
    void SetCreationProperties(object entityEntry);

    /// <summary>更新本次修改的审计属性。</summary>
    /// <param name="entityEntry">实体变更追踪条目。</param>
    void SetModificationProperties(object entityEntry);

    /// <summary>填充未设置的软删除审计属性。</summary>
    /// <param name="entityEntry">实体变更追踪条目。</param>
    void SetDeletionProperties(object entityEntry);
}
