using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;

namespace CompanyName.ProjectName.Domain.Users.Repositories;

/// <summary>角色聚合的仓储：在通用仓储之外提供被多个用例复用的角色专属查询。</summary>
public interface IRoleRepository : IRepository<Role, Guid>
{
    /// <summary>取当前租户标记为默认的角色，按名称排序；没有配置默认角色时为空列表。</summary>
    /// <remarks>
    /// 供注册、首次外部登录与管理员未指定角色的建用户共用；选定的角色经 <c>User.AssignRoles</c> 写成成员关系。
    /// </remarks>
    Task<List<Role>> GetDefaultRolesAsync(CancellationToken cancellationToken = default);
}
