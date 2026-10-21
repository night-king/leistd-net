#if (!LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Entities;

#endif
namespace CompanyName.ProjectName.Application.Initialization;

/// <summary>
/// 系统初始化器接口
/// 负责初始化系统角色、用户、权限等基础数据
/// </summary>
public interface ISystemInitializer
{
    /// <summary>初始化系统基础数据。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
#if (!LocalIdentity)

    /// <summary>在调用方已持有初始化锁、并由调用方提交的事务里初始化系统基础数据。</summary>
    /// <remarks>供部署引导命令使用：它要把首次初始化与后续授权放进同一事务，锁须覆盖到提交之后。</remarks>
    /// <returns>Admin 角色（已有的或本次创建、尚未提交的）。</returns>
    Task<Role> InitializeWithinLockAsync(CancellationToken cancellationToken = default);
#endif
}
