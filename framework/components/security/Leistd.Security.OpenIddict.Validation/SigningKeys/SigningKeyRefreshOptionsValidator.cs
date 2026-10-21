using Microsoft.Extensions.Options;

namespace Leistd.Security.OpenIddict.Validation.SigningKeys;

internal sealed class SigningKeyRefreshOptionsValidator(string sectionPath) : IValidateOptions<SigningKeyRefreshOptions>
{
    public ValidateOptionsResult Validate(string? name, SigningKeyRefreshOptions options)
    {
        var failures = new List<string>();
        if (options.FetchTimeout <= TimeSpan.Zero || options.FetchTimeout > TimeSpan.FromMilliseconds(int.MaxValue))
            failures.Add($"{sectionPath}:FetchTimeout must be positive and within HttpClient's supported range.");
        if (options.MinimumInterval <= TimeSpan.Zero) failures.Add($"{sectionPath}:MinimumInterval must be positive.");
        if (!AppContext.TryGetSwitch("Switch.Microsoft.IdentityModel.UpdateConfigAsBlocking", out var enabled) || !enabled)
            failures.Add("Switch.Microsoft.IdentityModel.UpdateConfigAsBlocking must be enabled explicitly by the host.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
