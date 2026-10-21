using Microsoft.Extensions.Options;

namespace Leistd.Security.OneTimeCodes.VerificationCodes;

internal sealed class VerificationCodeOptionsValidator(string sectionPath) : IValidateOptions<VerificationCodeOptions>
{
    public ValidateOptionsResult Validate(string? name, VerificationCodeOptions options) =>
        string.IsNullOrWhiteSpace(options.Key) || options.IsKeyUsable
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail([$"{sectionPath}:Key must contain at least {VerificationCodeOptions.MinimumKeyBytes} base64-encoded bytes."]);
}
