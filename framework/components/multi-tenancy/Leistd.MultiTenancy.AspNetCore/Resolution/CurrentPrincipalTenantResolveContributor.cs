using Microsoft.AspNetCore.Http;
using Leistd.MultiTenancy.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Leistd.Security.Claims;

namespace Leistd.MultiTenancy.AspNetCore.Resolution;

/// <summary>从已认证主体的租户声明解析租户。</summary>
/// <remarks>
/// 必须位于解析链首；无租户声明表示宿主用户，且认证主体一旦处理便不允许后续来源改写租户。
/// </remarks>
public class CurrentPrincipalTenantResolveContributor : ITenantResolveContributor
{
    /// <inheritdoc />
    public string Name => "CurrentPrincipal";

    /// <inheritdoc />
    public Task ResolveAsync(TenantResolveContext context)
    {
        var httpContext = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var user = httpContext?.User;

        if (user.HasAuthenticatedIdentity())
        {
            var claimTypes = context.ServiceProvider.GetRequiredService<IOptions<ClaimTypeOptions>>().Value;

            // 认证主体即使没有租户声明也必须终止解析，防止请求参数改写宿主身份。
            context.Handled = true;
            // 判定与非 HTTP 入口共用 TenantClaimReader，避免两处漂移。
            context.TenantIdOrName = TenantClaimReader.Read(user, claimTypes)?.ToString();
        }

        return Task.CompletedTask;
    }
}
