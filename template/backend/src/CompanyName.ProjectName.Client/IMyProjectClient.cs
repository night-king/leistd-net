using CompanyName.ProjectName.Client.Dtos;
using Refit;

namespace CompanyName.ProjectName.Client;

/// <summary>
/// 本服务的强类型调用客户端（Refit 接口，HTTP 实现由源生成器产出）。
/// 新增对外接口时在此补充方法与特性，并在 <c>Dtos/</c> 下补充对应 DTO。
/// </summary>
public interface IMyProjectClient
{
    /// <summary>获取服务基础信息（匿名端点，可用于探活与联调）。</summary>
    /// <param name="cancellationToken">取消令牌</param>
    [Get("/api/v1/service-info")]
    Task<ServiceInfoDto> GetServiceInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询本次调用在被调方呈现的身份（用户 + 调用方客户端），用于服务间调用联调。
    /// 需要自然人认证：服务间用户调用使用 Token Exchange；机器令牌不满足此端点策略。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    [Get("/api/v1/service-info/whoami")]
    Task<WhoAmIDto> WhoAmIAsync(CancellationToken cancellationToken = default);

    /// <summary>获取调用身份所在宿主或租户的平台统计（需要 <c>App.Dashboard</c>）。</summary>
    /// <param name="days">统计区间的天数，只接受 7 或 30</param>
    /// <param name="cancellationToken">取消令牌</param>
    [Get("/api/v1/platform/statistics")]
    Task<PlatformStatisticsDto> GetPlatformStatisticsAsync(
        [Query] int days = 7,
        CancellationToken cancellationToken = default);

    /// <summary>批量启用用户（需要 <c>App.Users.Update</c>）；逐条执行，部分失败不回滚。</summary>
    /// <param name="request">目标用户</param>
    /// <param name="cancellationToken">取消令牌</param>
    [Post("/api/v1/users/batch/enable")]
    Task<BatchOperationDto> BatchEnableUsersAsync(
        [Body] BatchUsersDto request,
        CancellationToken cancellationToken = default);

    /// <summary>批量禁用用户（需要 <c>App.Users.Update</c>）；逐条执行，部分失败不回滚。</summary>
    /// <param name="request">目标用户</param>
    /// <param name="cancellationToken">取消令牌</param>
    [Post("/api/v1/users/batch/disable")]
    Task<BatchOperationDto> BatchDisableUsersAsync(
        [Body] BatchUsersDto request,
        CancellationToken cancellationToken = default);
#if (LocalIdentity)

    /// <summary>批量解除用户的登录锁定（需要 <c>App.Users.Update</c>）；逐条执行，部分失败不回滚。</summary>
    /// <param name="request">目标用户</param>
    /// <param name="cancellationToken">取消令牌</param>
    [Post("/api/v1/users/batch/unlock")]
    Task<BatchOperationDto> BatchUnlockUsersAsync(
        [Body] BatchUsersDto request,
        CancellationToken cancellationToken = default);
#endif

    /// <summary>批量为用户追加角色（需要 <c>App.Users.ManageRoles</c>）；已有角色保留，逐条执行，部分失败不回滚。</summary>
    /// <param name="request">目标用户与要追加的角色</param>
    /// <param name="cancellationToken">取消令牌</param>
    [Post("/api/v1/users/batch/assign-roles")]
    Task<BatchOperationDto> BatchAssignUserRolesAsync(
        [Body] BatchAssignUserRolesDto request,
        CancellationToken cancellationToken = default);
}
