using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.MultiTenancy.Resolution;
using Leistd.MultiTenancy.Context;
using Leistd.Security.Claims;

namespace Leistd.MultiTenancy.AspNetCore.Middlewares;

/// <summary>解析并校验租户，然后在租户上下文中执行后续管道。</summary>
/// <remarks>
/// 放置顺序：<c>UseAuthentication()</c> 之后、<c>UseAuthorization()</c> 之前。
/// 解析出的租户不存在抛 <see cref="TenantNotFoundException"/>（404）；已停用时，已认证主体抛
/// <see cref="TenantNotActiveException"/>（403），未认证请求按 404 处理，不暴露启用状态。
/// 未解析出租户不是错误，即宿主上下文。
/// 资源服务与宿主共用本中间件，差别只在 <c>MultiTenancyOptions.ValidateResolvedTenant</c>。
/// </remarks>
public class MultiTenancyMiddleware(RequestDelegate next, ILogger<MultiTenancyMiddleware> logger)
{
    /// <summary>处理当前请求。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<ITenantResolver>();
        var result = await resolver.ResolveAsync();

        // 记录是哪个来源确定了租户：排查"租户怎么来的"只需要这一个名字。
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Tenant resolved by {AppliedResolver} and produced {TenantIdOrName}",
                result.AppliedResolver ?? "<none>",
                result.TenantIdOrName ?? "<host>");
        }

        if (result.TenantIdOrName is null)
        {
            await next(context);
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;

        Guid tenantId;
        string? tenantName = null;

        if (options.ValidateResolvedTenant)
        {
            var tenant = await FindTenantAsync(context, result.TenantIdOrName);

            // 未认证请求不区分“不存在”与“已停用”，不暴露启用状态；也不放行到宿主上下文，
            // 否则租户用户的凭据会拿去和宿主用户比对。
            // 存在性仍可被匿名判定（存在时请求走到业务逻辑，不存在时这里 404）：抹平它需要为不存在的租户
            // 编造注册策略，代价更大。这是有意接受的边界，见组件文档。
            if (tenant is null || !tenant.IsActive)
            {
                if (!context.User.HasAuthenticatedIdentity())
                {
                    throw new TenantNotFoundException(result.TenantIdOrName);
                }

                // 已认证主体只能探到自己的租户，明确报错对运维有价值
                throw tenant is null
                    ? new TenantNotFoundException(result.TenantIdOrName)
                    : new TenantNotActiveException(result.TenantIdOrName);
            }

            tenantId = tenant.Id;
            tenantName = tenant.Name;
        }
        else
        {
            // 该模式只采信已验证主体的租户 Id；没有注册表可查，名称保持 null。
            if (!Guid.TryParse(result.TenantIdOrName, out tenantId) || tenantId == Guid.Empty)
            {
                throw new TenantNotFoundException(result.TenantIdOrName);
            }
        }

        var currentTenant = context.RequestServices.GetRequiredService<ICurrentTenant>();

        // 租户上下文必须覆盖整个下游管道；日志作用域随 Change 一起打开。
        using (currentTenant.Change(tenantId, tenantName))
        {
            await next(context);
        }
    }

    private static async Task<TenantConfiguration?> FindTenantAsync(HttpContext context, string tenantIdOrName)
    {
        // 启动期已由 TenantStoreRegistrationValidator 校验；这里只兜底绕过 AddMultiTenancy 自行拼装管道的接法。
        var store = context.RequestServices.GetRequiredService<ITenantStore>();

        if (Guid.TryParse(tenantIdOrName, out var tenantId))
        {
            return await store.FindAsync(tenantId, context.RequestAborted);
        }

        var normalizer = context.RequestServices.GetRequiredService<ITenantNormalizer>();
        return await store.FindByNameAsync(normalizer.NormalizeName(tenantIdOrName)!, context.RequestAborted);
    }
}
