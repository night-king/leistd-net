using Microsoft.AspNetCore.Http;
using Leistd.MultiTenancy.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Leistd.MultiTenancy.AspNetCore.Options;

namespace Leistd.MultiTenancy.AspNetCore.Resolution;

/// <summary>从主机名解析租户，格式形如 <c>{0}.example.com</c>（<c>acme.example.com</c> → <c>acme</c>）。</summary>
/// <remarks>
/// 排在 Claim 贡献者之后、请求头与查询串之前：已认证主体的租户仍由 claim 定案；匿名请求的域名结论不被请求头改写。
/// 只把用于租户的通配子域指向本应用，否则 <c>www.example.com</c> 会被解析成名为 <c>www</c> 的租户。
/// </remarks>
public class DomainTenantResolveContributor : ITenantResolveContributor
{
    private const string TenantPlaceholder = "{0}";

    /// <inheritdoc />
    public string Name => "Domain";

    /// <inheritdoc />
    public Task ResolveAsync(TenantResolveContext context)
    {
        var options = context.ServiceProvider.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.DomainFormat))
        {
            return Task.CompletedTask;
        }

        var httpContext = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (httpContext is null)
        {
            return Task.CompletedTask;
        }

        // 移除 DNS 根点，防止等价主机名绕过受管域判定。
        var host = httpContext.Request.Host.Host.TrimEnd('.');

        switch (Match(host, options.DomainFormat, out var tenant))
        {
            case HostMatch.Tenant:
                context.TenantIdOrName = tenant;
                break;

            case HostMatch.ManagedWithoutTenant:
                // 受管域内定案为宿主，不允许后续请求头改写。
                context.Handled = true;
                break;

            case HostMatch.Outside:
                break;
        }

        return Task.CompletedTask;
    }

    private enum HostMatch
    {
        Outside,

        ManagedWithoutTenant,

        Tenant
    }

    // 受管域中缺少合法租户与域外请求的后续解析语义不同。
    private static HostMatch Match(string host, string format, out string? tenant)
    {
        tenant = null;

        var placeholderIndex = format.IndexOf(TenantPlaceholder, StringComparison.Ordinal);
        if (placeholderIndex < 0)
        {
            return HostMatch.Outside;
        }

        // 按 DNS label 边界取基础域，避免段内前后缀改变受管范围。
        var afterPlaceholder = format[(placeholderIndex + TenantPlaceholder.Length)..];
        var baseDomainStart = afterPlaceholder.IndexOf('.');
        if (baseDomainStart < 0)
        {
            // 对绕过启动校验的无基础域格式失败关闭。
            return HostMatch.Outside;
        }

        var labelPrefix = format[..placeholderIndex];
        var labelSuffix = afterPlaceholder[..baseDomainStart];
        var baseDomain = afterPlaceholder[(baseDomainStart + 1)..];

        if (host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase))
        {
            return HostMatch.ManagedWithoutTenant;
        }

        if (!host.EndsWith($".{baseDomain}", StringComparison.OrdinalIgnoreCase))
        {
            return HostMatch.Outside;
        }

        var leading = host[..^(baseDomain.Length + 1)];
        if (!leading.StartsWith(labelPrefix, StringComparison.OrdinalIgnoreCase) ||
            !leading.EndsWith(labelSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return HostMatch.ManagedWithoutTenant;
        }

        var tenantLength = leading.Length - labelPrefix.Length - labelSuffix.Length;
        if (tenantLength <= 0)
        {
            return HostMatch.ManagedWithoutTenant;
        }

        var candidate = leading.Substring(labelPrefix.Length, tenantLength);

        if (candidate.Contains('.'))
        {
            return HostMatch.ManagedWithoutTenant;
        }

        tenant = candidate;
        return HostMatch.Tenant;
    }
}
