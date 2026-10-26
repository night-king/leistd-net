#if (OpenIddictServer)
using CompanyName.ProjectName.Api.Filters;
using CompanyName.ProjectName.Application.Settings.AppServices;
using CompanyName.ProjectName.Application.Settings.Constants;
using CompanyName.ProjectName.Application.Settings.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>资源服务以机器身份按租户与用户读取账户偏好：给收件人产出文本、以及当前用户 <c>/me</c> 的条件重验证。</summary>
/// <remarks>
/// 只认机器策略，类上不标 <c>[Authorize]</c>（命名策略不叠加默认策略，叠加后要求"既是自然人又是机器"，一律被拒）。
/// 机器调用不带环境租户，租户在路径里显式给出。
/// </remarks>
[Route("api/v1/internal/account-preferences")]
public sealed class InternalAccountPreferenceController(IAccountPreferenceAppService accountPreferenceAppService) : BaseController
{
    /// <summary>读取指定租户内指定用户的完整表示，带强 ETag；<c>If-None-Match</c> 命中时回 304。</summary>
    /// <remarks>租户无效或用户不存在时回 404（<c>User:NotFound</c>）。</remarks>
    /// <param name="tenant"><c>host</c> 或租户 Id。</param>
    /// <param name="userId">用户 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [Authorize(Policy = AccountPreferencePolicies.Read)]
    [HttpGet("{tenant}/{userId:guid}")]
    [StrongETag]
    public async Task<AccountPreferencesOutputDto> GetAsync(string tenant, Guid userId, CancellationToken cancellationToken)
    {
        return await accountPreferenceAppService.GetForTenantUserAsync(tenant, userId, cancellationToken);
    }
}
#endif
