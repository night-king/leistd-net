namespace Leistd.Auditing.Abstractions;

/// <summary>标记具有创建时间的对象。</summary>
public interface IHasCreationTime
{
    /// <summary>创建时间。</summary>
    DateTime CreationTime { get; }
}

/// <summary>标记具有创建者信息的对象。</summary>
public interface ICreationAuditedObject : IHasCreationTime
{
    /// <summary>创建者标识；匿名或机器主体创建时为 <see langword="null"/>。</summary>
    string? CreatorId { get; }
}

/// <summary>标记具有最后修改时间的对象。</summary>
public interface IHasModificationTime
{
    /// <summary>最后修改时间；未修改过时为 <see langword="null"/>。</summary>
    DateTime? LastModificationTime { get; }
}

/// <summary>标记具有最后修改者信息的对象。</summary>
public interface IModificationAuditedObject : IHasModificationTime
{
    /// <summary>最后修改者标识。</summary>
    string? LastModifierId { get; }
}

/// <summary>标记具有删除时间的对象。</summary>
public interface IHasDeletionTime
{
    /// <summary>删除时间；未删除时为 <see langword="null"/>。</summary>
    DateTime? DeletionTime { get; }
}

/// <summary>标记支持软删除的对象。</summary>
public interface ISoftDelete
{
    /// <summary>是否已软删除；DDD 基座的全局查询过滤器默认排除为 <see langword="true"/> 的行。</summary>
    bool IsDeleted { get; }
}

/// <summary>标记具有删除审计信息的对象。</summary>
public interface IDeletionAuditedObject : IHasDeletionTime, ISoftDelete
{
    /// <summary>删除者标识。</summary>
    string? DeleterId { get; }
}

/// <summary>组合创建、修改和删除审计契约。</summary>
public interface IFullAuditedObject :
    ICreationAuditedObject,
    IModificationAuditedObject,
    IDeletionAuditedObject
{
}
