using Microsoft.Extensions.Options;

namespace Leistd.ServiceClient.OAuth.Options;

internal sealed class ServiceAuthenticationOptionsValidator(string path) : IValidateOptions<ServiceAuthenticationOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath => path;

    public ValidateOptionsResult Validate(string? name, ServiceAuthenticationOptions value)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(value.Authority, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add($"{path}:Authority must be an absolute HTTP(S) issuer URI.");
        if (string.IsNullOrWhiteSpace(value.ClientId)) errors.Add($"{path}:ClientId is required.");
        if (string.IsNullOrWhiteSpace(value.ClientSecret)) errors.Add($"{path}:ClientSecret is required.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
