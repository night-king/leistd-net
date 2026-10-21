using Leistd.Settings.Options;
using Microsoft.Extensions.Localization;

namespace Leistd.Settings.Definitions;

// 显示名的唯一取法：宿主资源里的 Setting:{Name} → 定义上的 DisplayName → Name。设置列表与写入错误提示共用。
// 定义是单例，翻译须在请求阶段进行，否则先到请求的语言会固化给所有人。
internal static class SettingDisplayNames
{
    public static IStringLocalizer? CreateLocalizer(
        IStringLocalizerFactory? localizerFactory,
        SettingManagementOptions? options)
        => options?.LocalizationResource is { } resource ? localizerFactory?.Create(resource) : null;

    public static string Resolve(ISettingDefinition definition, IStringLocalizer? localizer)
        => Localize(localizer, "Setting:" + definition.Name)
            ?? (string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName);

    public static string? Localize(IStringLocalizer? localizer, string key)
    {
        if (localizer is null)
            return null;

        var localized = localizer[key];
        return localized.ResourceNotFound ? null : localized.Value;
    }
}
