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
}
