using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Api.Filters;
using CompanyName.ProjectName.Application.Settings.AppServices;
using CompanyName.ProjectName.Application.Settings.Constants;
using CompanyName.ProjectName.Application.Settings.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>当前用户改自己的账户偏好（语言、时区、外观）。</summary>
/// <remarks>
/// 请求体是 JSON 合并补丁（RFC 7396），一次只含一个字段：缺省不变，<see langword="null"/> 清除本人的覆盖。
/// 一次只收一个字段：设置组件按项独立提交，多字段无法保证「全部校验通过后一次写入」，界面也只会一次改一项。
/// 成功时返回写入提交后的完整表示（含组织或部署基线的回落）与 ETag，前端直接用它，不再另读 <c>/me</c>。
/// </remarks>
[Authorize]
[Route("api/v1/auth/me/preferences")]
public sealed class AccountPreferenceController(IAccountPreferenceAppService accountPreferenceAppService) : BaseController
{
    /// <summary>写入一项偏好。</summary>
    /// <remarks>请求体形状不合法（不止一个字段、未知字段、值不是字符串或 <see langword="null"/>）是字段校验错误（400）。</remarks>
    /// <param name="body">合并补丁。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPatch]
    [Consumes("application/merge-patch+json", "application/json")]
    [StrongETag]
    public async Task<AccountPreferencesOutputDto> UpdateAsync([FromBody] JsonObject body, CancellationToken cancellationToken)
    {
        return await accountPreferenceAppService.UpdateForCurrentUserAsync(ToInput(body), cancellationToken);
    }

    // 合并补丁是传输格式：在这里按协议转成单字段的入参，取值是否允许由应用层判定
    private static UpdateAccountPreferenceInputDto ToInput(JsonObject body)
    {
        if (body.Count != 1)
        {
            throw Invalid("Send exactly one of the preference fields.", nameof(body));
        }

        var (field, node) = body.First();
        if (!AccountPreferenceFields.SettingNames.ContainsKey(field))
        {
            throw Invalid("Unknown preference field.", field);
        }

        if (node is not null && node.GetValueKind() != JsonValueKind.String)
        {
            throw Invalid("The value must be a string, or null to clear it.", field);
        }

        return new UpdateAccountPreferenceInputDto { Field = field, Value = node?.GetValue<string>() };
    }

    private static ValidationException Invalid(string message, string member)
        => new(new ValidationResult(message, [member]), validatingAttribute: null, value: null);
}
