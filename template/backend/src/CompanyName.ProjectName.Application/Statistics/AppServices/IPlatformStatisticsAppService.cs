using CompanyName.ProjectName.Application.Statistics.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Statistics.AppServices;

/// <summary>平台统计应用服务接口。</summary>
public interface IPlatformStatisticsAppService : IAppService
{
    /// <summary>获取当前上下文（宿主或本租户）的仪表盘统计。</summary>
    /// <remarks>
    /// 范围就是全局租户过滤器给出的当前层：宿主只看宿主的数据，租户管理员只看本租户；不关闭过滤器、不遍历租户库。
    /// </remarks>
    Task<PlatformStatisticsOutputDto> GetAsync(
        GetPlatformStatisticsInputDto input,
        CancellationToken cancellationToken = default);
}
