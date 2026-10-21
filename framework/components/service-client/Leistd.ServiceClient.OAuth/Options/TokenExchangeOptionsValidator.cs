using Microsoft.Extensions.Options;

namespace Leistd.ServiceClient.OAuth.Options;

internal sealed class TokenExchangeOptionsValidator(string clientName, string path) : IValidateOptions<TokenExchangeOptions>
{
    public ValidateOptionsResult Validate(string? name, TokenExchangeOptions value)
    {
        if (name != clientName) return ValidateOptionsResult.Skip;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(value.Audience)) errors.Add($"{path}:Audience is required.");
        if (string.IsNullOrWhiteSpace(value.Scope)) errors.Add($"{path}:Scope is required.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
