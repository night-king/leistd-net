using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 越界值启动即失败：TTL 同时决定改路由前的排空等待
internal sealed class TenantRouteCacheOptionsValidator(string configSectionPath) : IValidateOptions<TenantRouteCacheOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath { get; } = configSectionPath;

    public ValidateOptionsResult Validate(string? name, TenantRouteCacheOptions options)
    {
        var failures = new List<string>();
        if (options.CacheLifetime <= TimeSpan.Zero || options.CacheLifetime > TenantRouteCacheOptions.MaximumCacheLifetime)
            failures.Add($"{ConfigSectionPath}:CacheLifetime must be greater than zero and at most {TenantRouteCacheOptions.MaximumCacheLifetime}.");
        if (options.MaxStaleness < TimeSpan.Zero || options.MaxStaleness > TenantRouteCacheOptions.MaximumCacheLifetime)
            failures.Add($"{ConfigSectionPath}:MaxStaleness must be between zero and {TenantRouteCacheOptions.MaximumCacheLifetime}.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
