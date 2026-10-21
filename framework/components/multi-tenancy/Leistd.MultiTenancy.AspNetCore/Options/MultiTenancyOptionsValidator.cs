using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.AspNetCore.Options;

// 子域名格式错误会静默回退到其他解析来源，因此必须在启动期拒绝。
// 报错里的键名按实际绑定的配置节给出，宿主改了节路径时照提示去改才对得上
internal sealed class MultiTenancyOptionsValidator(string sectionPath = MultiTenancyOptions.SectionName) : IValidateOptions<MultiTenancyOptions>
{
    private const string TenantPlaceholder = "{0}";

    private const string SampleLabel = "t";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MultiTenancyOptions options)
    {
        var format = options.DomainFormat;
        if (string.IsNullOrWhiteSpace(format))
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        var first = format.IndexOf(TenantPlaceholder, StringComparison.Ordinal);
        if (first < 0)
        {
            failures.Add($"the tenant placeholder '{TenantPlaceholder}' is missing, e.g. '{{0}}.example.com'.");
        }
        else if (format.IndexOf(TenantPlaceholder, first + TenantPlaceholder.Length, StringComparison.Ordinal) >= 0)
        {
            failures.Add($"'{TenantPlaceholder}' occurs more than once, so the tenant label is ambiguous.");
        }

        // 占位符所在段之后必须还有固定的基础域：example.{0} 没有可比对的受管域，
        // 运行期只能要么谁都不匹配、要么把所有主机都圈进来，两种都是错的
        if (first >= 0 && !format[(first + TenantPlaceholder.Length)..].Contains('.'))
        {
            failures.Add($"a fixed base domain must follow the label containing '{TenantPlaceholder}', e.g. '{{0}}.example.com'.");
        }

        // Uri.CheckHostName 会放行部分浏览器不会发送的 Host，因此按 label 校验。
        var probe = format.Replace(TenantPlaceholder, SampleLabel, StringComparison.Ordinal);
        failures.AddRange(HostNameFailures(probe));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures.Select(failure
                => $"{sectionPath}:DomainFormat is invalid ('{format}'): {failure}"));
    }

    // 按浏览器实际发送的 ASCII/punycode Host 形态校验。
    private static IEnumerable<string> HostNameFailures(string host)
    {
        if (host.Any(c => c > 127))
        {
            yield return "it contains non-ASCII characters; use the punycode form (xn-- prefix) that browsers actually send.";
            yield break;
        }

        if (host.Length > 253)
        {
            yield return "the host name exceeds 253 characters.";
        }

        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            yield return "at least two labels are required, e.g. '{0}.example.com'.";
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                yield return $"label '{label}' is empty or exceeds 63 characters.";
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
            {
                yield return $"label '{label}' must start and end with a letter or digit.";
                continue;
            }

            if (label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            {
                yield return $"label '{label}' may contain only letters, digits and hyphens.";
            }
        }
    }
}
