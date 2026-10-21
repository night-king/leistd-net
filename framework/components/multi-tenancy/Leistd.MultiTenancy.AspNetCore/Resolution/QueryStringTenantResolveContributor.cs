using Microsoft.AspNetCore.Http;
using Leistd.MultiTenancy.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Leistd.MultiTenancy.AspNetCore.Options;

namespace Leistd.MultiTenancy.AspNetCore.Resolution;

/// <summary>从查询参数解析匿名请求的租户。</summary>
public class QueryStringTenantResolveContributor : ITenantResolveContributor
{
    /// <inheritdoc />
    public string Name => "QueryString";

    /// <inheritdoc />
    public Task ResolveAsync(TenantResolveContext context)
    {
        var httpContext = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (httpContext is null)
        {
            return Task.CompletedTask;
        }

        var options = context.ServiceProvider.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;
        var value = httpContext.Request.Query[options.QueryStringParameterName].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(value))
        {
            context.TenantIdOrName = value;
        }

        return Task.CompletedTask;
    }
}
