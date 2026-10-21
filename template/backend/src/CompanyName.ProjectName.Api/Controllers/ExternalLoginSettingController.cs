#if (ExternalLogin)
using CompanyName.ProjectName.Api.Auth.Authentication;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Infrastructure.Auth.OAuth.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>系统设置「外部登录」分组的只读状态：各提供方是否在本实例生效、缺什么，以及回调地址。</summary>
/// <remarks>
/// 开关与凭据的读写走设置组件的通用端点；这里补通用设置读不出的两件事——机密不回显，密钥来自部署配置时
/// 看不出已配置；回调地址要登记到提供方。放在 Api：它读的是认证方案目录与 Infrastructure 的选项。
/// </remarks>
[Authorize]
[Authorize(Policy = PermissionConstant.Settings.ExternalLogin)]
[Route("api/v1/settings/external-login")]
public sealed class ExternalLoginSettingController(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitor<ExternalAuthOptions> options) : BaseController
{
    /// <summary>各提供方的生效状态（宿主，需要外部登录设置的管理权限）。</summary>
    [HttpGet("status")]
    public async Task<ExternalLoginStatusOutputDto> GetStatusAsync()
    {
        var current = options.CurrentValue;
        return new ExternalLoginStatusOutputDto
        {
            Providers =
            [
                await StatusAsync(ExternalSchemeSynchronizer.Github, current.Github),
                await StatusAsync(ExternalSchemeSynchronizer.Google, current.Google)
            ]
        };
    }

    private async Task<ExternalLoginProviderStatusOutputDto> StatusAsync(string provider, ExternalAuthOptions.ProviderOptions settings) => new()
    {
        Provider = provider,
        Enabled = settings.Enabled,
        HasClientId = settings.HasClientId,
        HasClientSecret = settings.HasClientSecret,
        Active = await schemes.GetSchemeAsync(AuthenticationSchemeNames.ExternalProviderPrefix + provider) is not null,
        // 与官方处理器拼 redirect_uri 的规则一致：请求的 scheme、host、PathBase 加回调路径（可信转发头已在管道前段生效）
        CallbackUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{ExternalAuthenticationExtensions.CallbackPath(provider)}"
    };
}
#endif
