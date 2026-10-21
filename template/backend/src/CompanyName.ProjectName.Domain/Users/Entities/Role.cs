using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.MultiTenancy.Tenancy;

namespace CompanyName.ProjectName.Domain.Users.Entities;

/// <summary>角色实体。</summary>
public class Role : FullAuditedEntity<Guid>, IAggregateRoot<Guid>, IMultiTenant
{
    /// <summary>所属租户（null 为宿主角色），由多租户落值拦截器在创建时填充。</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>角色名称（Admin, Member 等，租户内唯一）。</summary>
    public string Name { get; private set; }

    /// <summary>显示名称。</summary>
    public string DisplayName { get; private set; }

    /// <summary>描述。</summary>
    public string? Description { get; private set; }

    /// <summary>是否系统内置角色（不可删除）。</summary>
    public bool IsStatic { get; private set; }

    /// <summary>是否默认角色（新用户自动分配）。</summary>
    public bool IsDefault { get; private set; }

    /// <summary>排序。</summary>
    public int Sort { get; private set; }

    private Role()
    {
        Name = null!;
        DisplayName = null!;
    }

    public Role(
        string name,
        string displayName,
        string? description = null,
        bool isStatic = false,
        bool isDefault = false,
        int sort = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Id = Guid.CreateVersion7();
        Name = name;
        DisplayName = displayName;
        Description = description;
        IsStatic = isStatic;
        IsDefault = isDefault;
        Sort = sort;
    }

    public void Update(string displayName, string? description, int sort)
    {
        DisplayName = displayName;
        Description = description;
        Sort = sort;
    }

    public void SetAsDefault()
    {
        IsDefault = true;
    }

    public void UnsetDefault()
    {
        IsDefault = false;
    }

    public bool CanBeDeleted()
    {
        return !IsStatic;
    }
}
