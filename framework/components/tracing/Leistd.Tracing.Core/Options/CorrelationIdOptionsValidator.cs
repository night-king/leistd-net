using Microsoft.Extensions.Options;

namespace Leistd.Tracing.Options;

internal sealed class CorrelationIdOptionsValidator(string sectionPath) : IValidateOptions<CorrelationIdOptions>
{
    public ValidateOptionsResult Validate(string? name, CorrelationIdOptions options) =>
        string.IsNullOrWhiteSpace(options.HeaderName)
            ? ValidateOptionsResult.Fail($"{sectionPath}:HeaderName is required.")
            : ValidateOptionsResult.Success;
}
