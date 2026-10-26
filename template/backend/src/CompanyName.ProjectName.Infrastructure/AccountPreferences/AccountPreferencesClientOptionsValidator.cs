#if (RemoteTokenAuth)
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>两个时长都须为正，旧值保留期须长于新鲜期；越界时启动失败，消息给出实际键名。</summary>
internal sealed class AccountPreferencesClientOptionsValidator : IValidateOptions<AccountPreferencesClientOptions>
{
    public ValidateOptionsResult Validate(string? name, AccountPreferencesClientOptions options)
    {
        var section = AccountPreferencesClientOptions.SectionName;
        if (options.FreshFor <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{section}:{nameof(options.FreshFor)} must be positive.");
        }

        return options.RetainFor <= options.FreshFor
            ? ValidateOptionsResult.Fail($"{section}:{nameof(options.RetainFor)} must be longer than {section}:{nameof(options.FreshFor)}.")
            : ValidateOptionsResult.Success;
    }
}
#endif
