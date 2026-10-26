using CompanyName.ProjectName.Application.Settings.Provider;

namespace CompanyName.ProjectName.Application.Settings.Constants;

/// <summary>账户偏好在接口里的字段名（与 <c>preferences</c> 的 JSON 成员同名）及其对应的设置名。</summary>
public static class AccountPreferenceFields
{
#if (IncludeLocalization)
    public const string Language = "language";

#endif
    public const string TimeZone = "timeZone";

    public const string Theme = "theme";

    /// <summary>字段名到设置名；写入只接受这里列出的字段。</summary>
    public static readonly IReadOnlyDictionary<string, string> SettingNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
#if (IncludeLocalization)
        [Language] = SettingConstant.Display.Language,
#endif
        [TimeZone] = SettingConstant.Display.TimeZone,
        [Theme] = SettingConstant.Display.Theme,
    };
}
