using CompanyName.ProjectName.Application.Settings.Constants;
using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Application.Settings.Timing;
using Leistd.ExceptionHandling;
using Leistd.Settings.Errors;

namespace CompanyName.ProjectName.Application.Settings.Validators;

/// <summary>偏好取值的判定，持有方与资源服务共用：拒绝时给出同一个错误码与占位数据。</summary>
/// <remarks>
/// 资源服务转交前就要判定：远端拒绝的文案按错误码在本服务重新本地化，占位数据带不过来。
/// 持有方仍是最终权威，它的设置定义与 <c>TimeZoneSettingValidator</c> 照常执行。
/// </remarks>
public sealed class AccountPreferenceValueValidator
{
    /// <summary>值为 <see langword="null"/>（清除覆盖）总是允许；时区须是可用的 IANA 名，其余须在候选值里。</summary>
    /// <param name="input">已核对字段名的写入请求。</param>
    /// <exception cref="BusinessException">取值不被接受。</exception>
    public void Validate(UpdateAccountPreferenceInputDto input)
    {
        if (input.Value is not { } value)
        {
            return;
        }

        if (input.Field == AccountPreferenceFields.TimeZone)
        {
            if (!UserTimeZoneProvider.TryResolveId(value, out _))
            {
                throw new BusinessException(AppSettingErrorCodes.TimeZoneInvalid, $"'{value}' is not a valid IANA time zone id.")
                    .WithData("Value", value);
            }

            return;
        }

#if (IncludeLocalization)
        var allowed = input.Field == AccountPreferenceFields.Theme
            ? SettingConstant.Display.ThemeModes
            : SettingConstant.Display.SupportedLanguages;
#else
        var allowed = SettingConstant.Display.ThemeModes;
#endif
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            var candidates = string.Join(", ", allowed);
            throw new BusinessException(SettingErrorCodes.ValueNotAllowed, $"'{value}' is not an allowed value. Allowed: {candidates}.")
                .WithData("Value", value)
                .WithData("Allowed", candidates);
        }
    }
}
