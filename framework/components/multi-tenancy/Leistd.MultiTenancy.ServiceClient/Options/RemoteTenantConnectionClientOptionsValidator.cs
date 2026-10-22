using Leistd.MultiTenancy.ConnectionStrings;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.ServiceClient.Options;

// 回源全用相对地址，缺 BaseAddress 时启动即失败并报键名；只约束本存储的客户端，通用 ServiceClient 选项仍可留空。
internal sealed class RemoteTenantConnectionClientOptionsValidator(string configSectionPath)
    : IValidateOptions<RemoteTenantConnectionClientOptions>
{
    public ValidateOptionsResult Validate(string? name, RemoteTenantConnectionClientOptions options)
    {
        var failures = new List<string>();
        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _))
        {
            failures.Add(
                $"{configSectionPath}:{nameof(RemoteTenantConnectionClientOptions.BaseAddress)} is required by the remote " +
                "tenant connection store and must be an absolute URI of the control plane.");
        }

        if (options.DatabaseDirectoryCacheLifetime <= TimeSpan.Zero
            || options.DatabaseDirectoryCacheLifetime > TenantRouteCacheOptions.MaximumCacheLifetime)
        {
            failures.Add(
                $"{configSectionPath}:{nameof(RemoteTenantConnectionClientOptions.DatabaseDirectoryCacheLifetime)} must be greater than zero and " +
                $"at most {TenantRouteCacheOptions.MaximumCacheLifetime} (was '{options.DatabaseDirectoryCacheLifetime}').");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
