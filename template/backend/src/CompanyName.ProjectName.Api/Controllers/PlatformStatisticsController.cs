using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Statistics.AppServices;
using CompanyName.ProjectName.Application.Statistics.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>平台统计控制器：仪表盘的汇总数据。</summary>
[Authorize]
[Route("api/v1/platform/statistics")]
public sealed class PlatformStatisticsController(IPlatformStatisticsAppService platformStatisticsAppService) : BaseController
{
    /// <summary>获取当前上下文的平台统计（需要仪表盘权限）。</summary>
    [HttpGet]
    [Authorize(Policy = PermissionConstant.Dashboard.Default)]
    public async Task<PlatformStatisticsOutputDto> GetAsync(
        [FromQuery] GetPlatformStatisticsInputDto input,
        CancellationToken cancellationToken)
    {
        return await platformStatisticsAppService.GetAsync(input, cancellationToken);
    }
}
