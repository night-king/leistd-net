using CompanyName.ProjectName.Application.Roles.Dtos;
using CompanyName.ProjectName.Application.Users.Dtos;
#if (RemoteTokenAuth)
using CompanyName.ProjectName.Domain.Users.ValueObjects;
#endif
using Leistd.Ddd.Application.Contracts.AppServices;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Application.Users.AppServices;

/// <summary>用户应用服务接口。</summary>
public interface IUserAppService : IAppService
{
    /// <summary>获取用户列表（分页）。</summary>
    Task<PagedResult<UserManagementOutputDto>> GetPagedListAsync(
        GetUserPagedInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>获取用户详情。</summary>
    Task<UserManagementOutputDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
#if (RemoteTokenAuth)

    /// <summary>当前主体：身份资料取自签发方令牌，超管标记与角色取本服务的授权数据。</summary>
    /// <remarks>
    /// 签发方令牌里的超管与角色声明属于签发方，不授予本服务任何权限；本地还没有投影这个主体时两者为空。
    /// </remarks>
    Task<CurrentResourceUserOutputDto> GetCurrentResourceUserAsync(CancellationToken cancellationToken = default);

    /// <summary>把当前已认证主体投影成本地用户行：不存在就建，存在就按令牌刷新资料字段；未认证或非用户主体时不做任何事。</summary>
    /// <remarks>
    /// 在独立工作单元内提交，不随调用方的业务失败回滚。首次访问并发撞主键时重试一次，
    /// 仍失败则抛出，由调用方决定是否放行（见 <c>ResourceUserProvisioningMiddleware</c>）。
    /// </remarks>
    /// <returns>投影后本地用户行的访问状态（本服务的启停）；未认证或非用户主体时为 <see langword="null"/>。</returns>
    Task<UserAccessStatus?> EnsureCurrentUserProjectedAsync(CancellationToken cancellationToken = default);

    /// <summary>按主键读当前主体本地用户行的访问状态，只读、不投影。</summary>
    /// <returns>访问状态；本地没有这一行（投影未建成）或非用户主体时为 <see langword="null"/>。</returns>
    Task<UserAccessStatus?> GetCurrentUserAccessStatusAsync(CancellationToken cancellationToken = default);
#endif

#if (LocalIdentity)
    /// <summary>创建用户。</summary>
    Task<UserManagementOutputDto> CreateAsync(
        CreateUserInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>更新用户。</summary>
    /// <remarks>
    /// 资源服务形态没有这两个入口：用户行由令牌投影而来（见 <c>UserDomainService.EnsureProjectedAsync</c>），
    /// 资料字段归签发方所有，本地既不新建也不改。那一侧本服务自己拥有的是角色授予与启停。
    /// </remarks>
    Task<UserManagementOutputDto> UpdateAsync(
        Guid id,
        UpdateUserInputDto input,
        CancellationToken cancellationToken = default);

#endif

    /// <summary>启用用户。</summary>
    Task EnableAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>禁用用户。</summary>
    Task DisableAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>重置用户密码，并撤销该用户的全部会话（重置的是自己时保留当前会话）。</summary>
#if (LocalIdentity)
    Task ResetPasswordAsync(Guid id, ResetUserPasswordInputDto input, CancellationToken cancellationToken = default);

    /// <summary>解除用户的登录锁定（登录失败触发的临时锁定，或管理员锁定），并清零失败计数。</summary>
    Task UnlockAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>重置用户的两步验证（停用并清掉密钥与恢复码），该用户的会话全部失效。</summary>
    Task ResetTwoFactorAsync(Guid id, CancellationToken cancellationToken = default);
#endif

#if (LocalIdentity)
    /// <summary>删除用户（软删除）。</summary>
    /// <remarks>资源服务形态没有这个入口：删掉了下次持令牌访问又会被投影回来，是个会骗人的按钮。</remarks>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

#endif

    /// <summary>查询用户当前角色。</summary>
    Task<IReadOnlyList<RoleBriefOutputDto>> GetRolesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>取用户上传的头像图片；用户不存在、没有头像或头像是外部地址时为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 不要求用户管理权限：头像随用户名出现在各处（操作记录、成员列表、顶栏），已登录即可看；
    /// 查询仍受租户过滤器约束，看不到别的租户的用户。
    /// </remarks>
    Task<UserAvatarOutputDto?> GetAvatarAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>替换用户角色。需要 App.Users.ManageRoles。</summary>
    Task<IReadOnlyList<RoleBriefOutputDto>> ReplaceRolesAsync(
        Guid id,
        UpdateUserRolesInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>批量启用用户：逐条按单个启用的规则执行并留痕，部分失败不回滚已成功的条目。</summary>
    Task<BatchOperationOutputDto> BatchEnableAsync(
        BatchUsersInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>批量停用用户：逐条按单个停用的规则执行并留痕，部分失败不回滚已成功的条目。</summary>
    Task<BatchOperationOutputDto> BatchDisableAsync(
        BatchUsersInputDto input,
        CancellationToken cancellationToken = default);
#if (LocalIdentity)

    /// <summary>批量解除登录锁定：逐条按单个解锁的规则执行并留痕，部分失败不回滚已成功的条目。</summary>
    Task<BatchOperationOutputDto> BatchUnlockAsync(
        BatchUsersInputDto input,
        CancellationToken cancellationToken = default);
#endif

    /// <summary>批量为用户追加角色（不撤销已有角色）。需要 App.Users.ManageRoles。</summary>
    Task<BatchOperationOutputDto> BatchAssignRolesAsync(
        BatchAssignUserRolesInputDto input,
        CancellationToken cancellationToken = default);
}
