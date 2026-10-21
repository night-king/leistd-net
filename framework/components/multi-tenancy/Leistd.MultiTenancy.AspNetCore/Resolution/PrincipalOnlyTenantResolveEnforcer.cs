using Microsoft.Extensions.Logging;
using Leistd.MultiTenancy.Resolution;
using Microsoft.Extensions.Options;
using Leistd.MultiTenancy.AspNetCore.Options;

namespace Leistd.MultiTenancy.AspNetCore.Resolution;

// 关闭注册表校验时只允许已验证主体的 claim 解析租户。
// PostConfigure 在宿主配置后移除其他来源，对被忽略的配置记录告警。
internal sealed class PrincipalOnlyTenantResolveEnforcer(
    IOptions<MultiTenancyOptions> multiTenancyOptions,
    ILogger<PrincipalOnlyTenantResolveEnforcer> logger)
    : IPostConfigureOptions<TenantResolveOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, TenantResolveOptions options)
    {
        var multiTenancy = multiTenancyOptions.Value;
        if (multiTenancy.ValidateResolvedTenant)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(multiTenancy.DomainFormat))
        {
            logger.LogWarning(
                "MultiTenancyOptions.DomainFormat is set ('{DomainFormat}') but ValidateResolvedTenant is " +
                "false, so the resolve chain is narrowed to the principal contributor and subdomain " +
                "resolution will not run. Tenants come from the verified tenant claim only.",
                multiTenancy.DomainFormat);
        }

        options.Contributors.Clear();
        options.Contributors.Add(new CurrentPrincipalTenantResolveContributor());
    }
}
