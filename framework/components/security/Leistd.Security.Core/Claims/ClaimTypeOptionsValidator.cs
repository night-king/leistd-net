using Microsoft.Extensions.Options;

namespace Leistd.Security.Claims;

// 选项只由宿主在代码里配置，消息以"类型名.属性名"开头；按实现去重登记，重复注册不会让同一失败报多遍。
internal sealed class ClaimTypeOptionsValidator : IValidateOptions<ClaimTypeOptions>
{
    public ValidateOptionsResult Validate(string? name, ClaimTypeOptions options)
    {
        var failures = new List<string>();
        if (options.UserIds.Count == 0 || options.UserIds.Any(string.IsNullOrWhiteSpace))
            failures.Add("ClaimTypeOptions.UserIds must list at least one claim type and no blank entries.");
        if (string.IsNullOrWhiteSpace(options.TenantId))
            failures.Add("ClaimTypeOptions.TenantId must not be empty.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
