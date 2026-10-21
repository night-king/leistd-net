using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.Resolution;

/// <summary>按配置顺序执行租户解析贡献者。</summary>
/// <remarks>
/// 以 Scoped 注册：注入的 <paramref name="serviceProvider"/> 即当前请求作用域，
/// 贡献者可从中解析 <c>IHttpContextAccessor</c> 等服务。
/// </remarks>
public class TenantResolver(IServiceProvider serviceProvider, IOptions<TenantResolveOptions> options) : ITenantResolver
{
    /// <inheritdoc />
    public async Task<TenantResolveResult> ResolveAsync()
    {
        var result = new TenantResolveResult();
        var context = new TenantResolveContext(serviceProvider);

        foreach (var contributor in options.Value.Contributors)
        {
            await contributor.ResolveAsync(context);

            if (context.HasResolvedTenantOrHost())
            {
                result.TenantIdOrName = context.TenantIdOrName;
                result.AppliedResolver = contributor.Name;
                break;
            }
        }

        return result;
    }
}
