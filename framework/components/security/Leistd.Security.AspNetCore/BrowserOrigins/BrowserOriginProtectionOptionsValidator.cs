using Microsoft.Extensions.Options;

namespace Leistd.Security.AspNetCore.BrowserOrigins;

internal sealed class BrowserOriginProtectionOptionsValidator(string sectionPath) : IValidateOptions<BrowserOriginProtectionOptions>
{
    public ValidateOptionsResult Validate(string? name, BrowserOriginProtectionOptions options)
    {
        var failures = new List<string>();
        ValidatePaths(options.WritePaths, nameof(options.WritePaths));
        ValidatePaths(options.AllMethodPaths, nameof(options.AllMethodPaths));
        if ((options.WritePaths?.Length ?? 0) + (options.AllMethodPaths?.Length ?? 0) == 0)
            failures.Add($"{sectionPath}:WritePaths or {sectionPath}:AllMethodPaths must select at least one protected path.");
        if (options.CorsPolicyName is not null && string.IsNullOrWhiteSpace(options.CorsPolicyName))
            failures.Add($"{sectionPath}:CorsPolicyName must be null or nonblank.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void ValidatePaths(string[]? paths, string property)
        {
            if (paths is null) { failures.Add($"{sectionPath}:{property} must be an array."); return; }
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Length > 1 && path.EndsWith('/') || path.Any(char.IsWhiteSpace) || path.IndexOfAny(['?', '#', '\\']) >= 0)
                    failures.Add($"{sectionPath}:{property}:{index} must be a path prefix beginning with /, without trailing slash (except /), whitespace, query, fragment or backslash.");
            }
        }
    }
}
