using System.Globalization;
using Microsoft.Extensions.Options;

namespace Leistd.Localization.Options;

// 选项只由宿主在代码里配置，消息以"类型名.属性名"开头。
internal sealed class JsonLocalizationOptionsValidator : IValidateOptions<JsonLocalizationOptions>
{
    private const string Property = $"{nameof(JsonLocalizationOptions)}.{nameof(JsonLocalizationOptions.SupportedCultures)}";

    public ValidateOptionsResult Validate(string? name, JsonLocalizationOptions options)
    {
        if (options.SupportedCultures is not { Count: > 0 } cultures)
            return ValidateOptionsResult.Fail($"{Property} must contain at least one culture.");

        var errors = new List<string>();
        foreach (var culture in cultures)
        {
            if (!IsKnownCulture(culture))
                errors.Add($"{Property} contains '{culture}', which is not a known culture name.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsKnownCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
            return false;

        try
        {
            CultureInfo.GetCultureInfo(culture, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
