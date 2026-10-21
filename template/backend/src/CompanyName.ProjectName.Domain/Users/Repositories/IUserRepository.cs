using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;

namespace CompanyName.ProjectName.Domain.Users.Repositories;

/// <summary>用户聚合的仓储：在通用仓储之外提供被多个用例复用的用户专属查询。</summary>
public interface IUserRepository : IRepository<User, Guid>
{
    /// <summary>取用户已落库的角色名，按名称排序；没有角色时为空列表。</summary>
    /// <remarks>
    /// 供登录签发的角色声明、令牌主体与当前用户资料共用。按当前租户与软删除过滤：
    /// 已删除的角色或关联行不计入。工作单元内刚插入、尚未落库的关联行读不到，写路径直接用自己刚分配的角色名。
    /// </remarks>
    Task<List<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>按 Id 取用户并加载角色成员关系；不存在时为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 修改角色前必须经它读取：<c>GetByIdAsync</c> 不加载成员关系，在它上面分配会重复插入、替换会漏撤。
    /// 关掉软删除过滤时，已撤销的成员关系一并加载。
    /// </remarks>
    Task<User?> GetWithRolesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>带角色成员关系的用户查询，供批量读取与按条件加载聚合。</summary>
    Task<IQueryable<User>> GetQueryableWithRolesAsync(CancellationToken cancellationToken = default);
}
