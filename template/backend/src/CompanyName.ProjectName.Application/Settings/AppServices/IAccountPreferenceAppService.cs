using CompanyName.ProjectName.Application.Settings.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Settings.AppServices;

/// <summary>账户偏好（语言、时区、外观）的用例。</summary>
/// <remarks>
/// 偏好的持有方（Identity、Standalone）写入本服务的用户层设置；资源服务以 Token Exchange 代表本人转交 Identity。
/// 读取经 <see cref="Preferences.IAccountPreferenceStore"/>，<c>/me</c> 与服务端产出文本都用它。
/// </remarks>
public interface IAccountPreferenceAppService : IAppService
{
    /// <summary>写入本人的一项偏好，返回写入提交后的完整表示。</summary>
    /// <remarks>只能写本人：入口不接受目标用户，主体取自当前请求。</remarks>
    /// <param name="input">要改的一项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AccountPreferencesOutputDto> UpdateForCurrentUserAsync(
        UpdateAccountPreferenceInputDto input,
        CancellationToken cancellationToken = default);
#if (OpenIddictServer)

    /// <summary>读取指定租户内指定用户的完整表示（资源服务的机器调用）。</summary>
    /// <param name="tenant"><c>host</c> 或租户 Id。</param>
    /// <param name="userId">用户 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Leistd.ExceptionHandling.BusinessException">租户无效（非法、停用、删除）或用户在其中不存在（<c>User:NotFound</c>），不退回宿主。</exception>
    Task<AccountPreferencesOutputDto> GetForTenantUserAsync(
        string tenant,
        Guid userId,
        CancellationToken cancellationToken = default);
#endif
}
