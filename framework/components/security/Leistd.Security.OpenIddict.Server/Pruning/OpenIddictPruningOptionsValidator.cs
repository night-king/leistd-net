using Microsoft.Extensions.Options;

namespace Leistd.Security.OpenIddict.Server.Pruning;

internal sealed class OpenIddictPruningOptionsValidator(string sectionPath) : IValidateOptions<OpenIddictPruningOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenIddictPruningOptions options) =>
        options.MinimumRetention >= TimeSpan.FromMinutes(10) ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail([$"{sectionPath}:MinimumRetention must be at least 10 minutes."]);
}
