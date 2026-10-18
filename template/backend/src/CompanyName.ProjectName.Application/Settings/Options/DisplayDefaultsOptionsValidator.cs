#if (IncludeLocalization)
using CompanyName.ProjectName.Application.Settings.Provider;
#endif
using CompanyName.ProjectName.Application.Settings.Timing;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Application.Settings.Options;

/// <summary>校验界面显示的部署基线：空值合法（跟随设备），非空值必须是设置本身也接受的值。</summary>
/// <remarks>与写入设置时同一套判定，否则配置里的默认值能生效、管理员却存不进同一个值。只用纯值规则，
/// 不依赖读取设置的服务：它们由设置定义构造，而定义又依赖本选项。</remarks>
public sealed class DisplayDefaultsOptionsValidator : IValidateOptions<DisplayDefaultsOptions>
{
    public ValidateOptionsResult Validate(string? name, DisplayDefaultsOptions options)
    {
        var failures = new List<string>();
#if (IncludeLocalization)
        if (!string.IsNullOrWhiteSpace(options.Language) &&
            !SettingConstant.Display.SupportedLanguages.Contains(options.Language.Trim(), StringComparer.Ordinal))
            failures.Add($"{DisplayDefaultsOptions.SectionName}:Language must be one of {string.Join(", ", SettingConstant.Display.SupportedLanguages)} or empty.");
#endif
        if (!string.IsNullOrWhiteSpace(options.TimeZone) && !UserTimeZoneProvider.TryResolveId(options.TimeZone.Trim(), out _))
            failures.Add($"{DisplayDefaultsOptions.SectionName}:TimeZone must be an IANA time zone id (for example Asia/Tokyo) or empty.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
