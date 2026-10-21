namespace Leistd.Ddd.Application.Contracts.Dtos;

/// <summary>实体 DTO 基类（可选）。</summary>
/// <typeparam name="TKey">主键类型。</typeparam>
public abstract record EntityDto<TKey>
{
    /// <summary>实体主键。</summary>
    public required TKey Id { get; init; }
}

/// <summary>主键为 <see cref="Guid"/> 的实体 DTO 基类（可选）。</summary>
public abstract record EntityDto : EntityDto<Guid>
{

}
