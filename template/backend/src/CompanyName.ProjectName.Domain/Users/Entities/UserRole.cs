using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.MultiTenancy.Tenancy;

namespace CompanyName.ProjectName.Domain.Users.Entities;

/// <summary>用户的角色成员关系：<see cref="User"/> 聚合的子实体，按 <see cref="RoleId"/> 引用角色聚合。</summary>
/// <remarks>
/// 只经 <see cref="User"/> 的方法分配与撤销，没有独立仓储。撤销是软删除：已撤销的行留作历史，
/// 资源管理员引导据此不再把撤销过的 Admin 成员关系加回来。
/// 自带租户维度：仓储里的角色名查询直接读这张表，全局租户过滤器要能单独作用于它。
/// </remarks>
public class UserRole : DeletionAuditedEntity<Guid>, IMultiTenant
{
    /// <summary>所属租户 ID；<see langword="null"/> 表示宿主。</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>用户 ID。</summary>
    public Guid UserId { get; private set; }

    /// <summary>角色 ID。</summary>
    public Guid RoleId { get; private set; }

    private UserRole() { }

    internal UserRole(Guid userId, Guid roleId)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        RoleId = roleId;
    }

    /// <summary>撤销这条成员关系：标记软删除，删除时间与删除者在保存时由审计拦截器补齐。</summary>
    internal void Revoke()
    {
        IsDeleted = true;
    }
}
