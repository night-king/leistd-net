using System.Security.Claims;
using Leistd.Security.Claims;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Api.Middlewares;

/// <summary>单租户入口只接受合法的宿主身份，先于用户投影和业务数据访问。</summary>
internal sealed class HostPrincipalMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, IOptions<ClaimTypeOptions> claimTypes)
    {
        if (context.User.Identities.Any(identity => identity.IsAuthenticated) && !IsHost(context.User, claimTypes.Value))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    }

    internal static bool IsHost(ClaimsPrincipal? principal, ClaimTypeOptions claimTypes)
    {
        var tenant = claimTypes.ReadTenant(principal);
        return tenant.IsValid && tenant.TenantId is null;
    }
}
