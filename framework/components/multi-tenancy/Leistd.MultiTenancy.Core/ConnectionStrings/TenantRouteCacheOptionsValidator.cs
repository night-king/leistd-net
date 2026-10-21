using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 越界值启动即失败：TTL 同时决定改路由前的排空等待
internal sealed class TenantRouteCacheOptionsValidator(string configSectionPath) : IValidateOptions<TenantRouteCacheOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath { get; } = configSectionPath;

    public ValidateOptionsResult Validate(string? name, TenantRouteCacheOptions options) =>
        options.CacheLifetime > TimeSpan.Zero && options.CacheLifetime <= TenantRouteCacheOptions.MaximumCacheLifetime
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ConfigSectionPath}:CacheLifetime must be greater than zero and " +
                $"at most {TenantRouteCacheOptions.MaximumCacheLifetime} (was '{options.CacheLifetime}'). " +
                "It determines how long a changed tenant route may still be served by warm instances, and therefore " +
                "how long the deactivate-and-drain step must wait before the route can be changed.");
}
