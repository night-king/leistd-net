#if (LocalIdentity)
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Settings.AppServices;
using CompanyName.ProjectName.Application.Settings.Dtos;
using Leistd.OperationRecords.AspNetCore.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>设置里的业务动作：发信测试。</summary>
/// <remarks>
/// 设置的读取与写入由设置组件映射在同一前缀下（见 <c>ComponentEndpoints</c>）；
/// 这里只放组件不认识的业务动作，路由与组件端点不重叠。
/// </remarks>
[Authorize]
[Route("api/v1/settings")]
public sealed class SettingController(IEmailSettingsAppService emailSettingsAppService) : BaseController
{
    /// <summary>用当前生效的发信参数发一封测试邮件（宿主，需要邮件发送设置的管理权限）。</summary>
    [HttpPost("email/test")]
    [Authorize(Policy = PermissionConstant.Settings.Email)]
    [OperationRecordAction(OperationRecordActions.SettingTestEmailSent)]
    public Task SendTestEmailAsync(
        [FromBody] SendTestEmailInputDto input,
        CancellationToken cancellationToken = default)
        => emailSettingsAppService.SendTestEmailAsync(input, cancellationToken);
}
#endif
