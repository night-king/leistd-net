using Microsoft.AspNetCore.Http;
using Leistd.MultiTenancy.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Leistd.MultiTenancy.AspNetCore.Options;

namespace Leistd.MultiTenancy.AspNetCore.Resolution;

/// <summary>从请求头解析匿名请求的租户。</summary>
/// <remarks>请求头只选择认证分区，不授予数据访问权限。</remarks>
public class HeaderTenantResolveContributor : ITenantResolveContributor
{
    /// <inheritdoc />
    public string Name => "Header";

    /// <inheritdoc />
    public Task ResolveAsync(TenantResolveContext context)
    {
        var httpContext = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (httpContext is null)
        {
            return Task.CompletedTask;
        }

        var options = context.ServiceProvider.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;
        var value = httpContext.Request.Headers[options.HeaderName].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(value))
        {
            context.TenantIdOrName = value;
        }

        return Task.CompletedTask;
    }
}
