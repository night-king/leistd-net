#if (OpenIddictServer)
using CompanyName.ProjectName.Application.Auth.OAuth;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Application.Auth.Options;

/// <summary>校验资源、所有者与 scope 的名称和冲突。</summary>
public sealed class OAuthResourceOptionsValidator : IValidateOptions<OAuthResourceOptions>
{
    public ValidateOptionsResult Validate(string? name, OAuthResourceOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Resource)) failures.Add("OAuth:Resource is required.");
        for (var index = 0; index < options.ApiResources.Length; index++)
        {
            var api = options.ApiResources[index];
            if (string.IsNullOrWhiteSpace(api.Name)) failures.Add($"OAuth:ApiResources:{index}:Name is required.");
            if (string.IsNullOrWhiteSpace(api.ScopeName)) failures.Add($"OAuth:ApiResources:{index}:Scope must not be empty.");
            if (string.IsNullOrWhiteSpace(api.Owner)) failures.Add($"OAuth:ApiResources:{index}:OwnerClientId must not be empty.");
        }
        if (options.ApiResources.GroupBy(api => api.Name, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
            options.ApiResources.Any(api => api.Name == options.Resource))
            failures.Add("OAuth:ApiResources names must be distinct from each other and OAuth:Resource.");
        if (OAuthScopes.All(options).GroupBy(scope => scope.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } conflict)
            failures.Add($"OAuth:ApiResources scope '{conflict.Key}' conflicts with another scope.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
#endif
