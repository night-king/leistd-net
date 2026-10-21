using Leistd.Data.Paging;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Resolution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Leistd.MultiTenancy.Management.Dtos;

namespace Leistd.MultiTenancy.AspNetCore.Endpoints;

/// <summary>租户管理与租户连接的 HTTP 端点。</summary>
public static class TenantManagementEndpoints
{
    /// <summary>端点名前缀，宿主按名字给个别端点追加约定时使用。</summary>
    public const string NamePrefix = "Leistd.MultiTenancy.";

    /// <summary>
    /// 映射租户管理：<c>GET /</c>、<c>GET /{id}</c>、<c>POST /</c>、<c>PUT /{id}</c>、<c>PUT /{id}/activation</c>、
    /// <c>DELETE /{id}</c>，以及匿名的 <c>GET /by-host</c>。
    /// </summary>
    /// <remarks>
    /// <para>创建请求体按 <typeparamref name="TCreateInput"/> 绑定：宿主派生 <see cref="CreateTenantInputDto"/>
    /// 携带开通需要的更多信息，开通器从上下文里取回。</para>
    /// <para>匿名端点只有 <c>by-host</c>：按宿主配置的解析链回答“这个请求会落在哪个租户”，只回租户名；
    /// 不提供按名称或账号查租户的匿名端点，避免暴露租户存在性、启用状态与账号归属。登录页拿租户名直接发登录请求即可。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapGroup("/api/v1/tenants").MapTenantManagement&lt;CreateTenantWithAdminInputDto&gt;(options =&gt;
    /// {
    ///     options.ReadPolicy = "App.Tenants";
    ///     options.CreatePolicy = "App.Tenants.Create";
    ///     options.UpdatePolicy = "App.Tenants.Update";
    ///     options.DeletePolicy = "App.Tenants.Delete";
    /// });
    /// </code>
    /// </example>
    /// <typeparam name="TCreateInput">创建请求体类型。</typeparam>
    /// <param name="endpoints">路由构建器（通常是宿主的路由组）。</param>
    /// <param name="configure">授权口径。</param>
    /// <returns>承载这组端点的路由组。</returns>
    public static RouteGroupBuilder MapTenantManagement<TCreateInput>(
        this IEndpointRouteBuilder endpoints,
        Action<TenantManagementEndpointOptions> configure)
        where TCreateInput : CreateTenantInputDto
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TenantManagementEndpointOptions();
        configure(options);
        options.Validate();

        // 不在组上套无参 RequireAuthorization：每个端点都已声明具名策略，再叠一层宿主默认策略
        // 等于替宿主给管理端点追加了它没要求过的主体条件（机器主体因此会被静默拒绝）。
        var group = endpoints.MapGroup(string.Empty);

        group.MapGet(string.Empty, (
                ITenantManagementService service,
                CancellationToken cancellationToken,
                int offset = 0,
                int limit = PageRequest.DefaultLimit,
                string? keyword = null,
                bool? isActive = null)
                => service.GetPagedAsync(
                    new GetTenantPagedInputDto { Offset = offset, Limit = limit, Keyword = keyword, IsActive = isActive },
                    cancellationToken))
            .WithName(NamePrefix + "GetTenants")
            .RequireAuthorization(options.ReadPolicy);

        group.MapGet("{id:guid}", (Guid id, ITenantManagementService service, CancellationToken cancellationToken)
                => service.GetAsync(id, cancellationToken))
            .WithName(NamePrefix + "GetTenant")
            .RequireAuthorization(options.ReadPolicy);

        group.MapPost(string.Empty, (TCreateInput input, ITenantManagementService service, CancellationToken cancellationToken)
                => service.CreateAsync(input, cancellationToken))
            .WithName(NamePrefix + "CreateTenant")
            .RequireAuthorization(options.CreatePolicy);

        group.MapPut("{id:guid}", (Guid id, UpdateTenantInputDto input, ITenantManagementService service, CancellationToken cancellationToken)
                => service.UpdateAsync(id, input, cancellationToken))
            .WithName(NamePrefix + "UpdateTenant")
            .RequireAuthorization(options.UpdatePolicy);

        group.MapPut("{id:guid}/activation", (
                Guid id,
                UpdateTenantActivationInputDto input,
                ITenantManagementService service,
                CancellationToken cancellationToken)
                => service.SetActivationAsync(id, input, cancellationToken))
            .WithName(NamePrefix + "SetTenantActivation")
            .RequireAuthorization(options.UpdatePolicy);

        group.MapDelete("{id:guid}", async (Guid id, ITenantManagementService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(NamePrefix + "DeleteTenant")
            .RequireAuthorization(options.DeletePolicy);

        group.MapGet("by-host", async (ITenantResolver resolver, ICurrentTenant currentTenant) =>
            {
                // 跑宿主配置的解析链而不是另起域名贡献者：宿主定制解析方式时，探测与真实请求必须给出同一个答案。
                var resolved = await resolver.ResolveAsync();

                if (resolved.TenantIdOrName is { Length: > 0 } tenantIdOrName)
                {
                    // 中间件已按同一条链校验过该租户（不存在或已停用已被拒）。匿名响应只回名字，不含标识与启用状态。
                    return new TenantByHostOutputDto
                    {
                        Decision = HostTenantDecision.Tenant,
                        Tenant = new AnonymousTenantOutputDto { Name = currentTenant.Name ?? tenantIdOrName }
                    };
                }

                // 有贡献者定案却没有租户，是"受管域内但不指向租户"，必须与"没有谁表态"分开回传
                return new TenantByHostOutputDto
                {
                    Decision = resolved.AppliedResolver is null ? HostTenantDecision.Undecided : HostTenantDecision.Host
                };
            })
            .WithName(NamePrefix + "FindTenantByHost")
            .AllowAnonymous();

        return group;
    }

    /// <summary>用 <see cref="CreateTenantInputDto"/> 作为创建请求体映射租户管理，见 <see cref="MapTenantManagement{TCreateInput}"/>。</summary>
    /// <param name="endpoints">路由构建器（通常是宿主的路由组）。</param>
    /// <param name="configure">授权口径。</param>
    /// <returns>承载这组端点的路由组。</returns>
    public static RouteGroupBuilder MapTenantManagement(
        this IEndpointRouteBuilder endpoints,
        Action<TenantManagementEndpointOptions> configure)
        => endpoints.MapTenantManagement<CreateTenantInputDto>(configure);

    /// <summary>
    /// 映射租户连接：<c>GET /{tenantId}</c>、<c>PUT /{tenantId}/{name}</c>、<c>DELETE /{tenantId}/{name}?expectedVersion=</c>，
    /// 以及配置了策略时的机器端点 <c>GET /runtime/{tenantId}?name=</c>、<c>GET /databases?name=&amp;activeOnly=</c>
    /// （前两者同属 <c>RuntimeReadPolicy</c>）与 <c>GET /migration?name=</c>（<c>MigrationReadPolicy</c>）。
    /// </summary>
    /// <remarks>
    /// <para>管理面只回名字与版本。<c>/runtime</c> 与 <c>/migration</c> 下发明文连接串，一次只回被问到的那个连接名；
    /// <c>/databases</c> 不下发连接串，只回指纹与租户归属，因此与 <c>/runtime</c> 同属读路由权限。
    /// 远端连接存储（<c>Leistd.MultiTenancy.ServiceClient</c>）按这三条路由回源。</para>
    /// <para>每个端点只挂自己的命名策略，路由组上不叠宿主的默认策略（默认策略通常要求自然人，会拒绝机器令牌）。
    /// 连接名不合法返回带码的 400；启用中的租户改变数据落点返回 409。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapGroup("/api/v1/tenant-connections").MapTenantConnections(options =&gt;
    /// {
    ///     options.ManagePolicy = "App.Tenants.Update";
    ///     options.RuntimeReadPolicy = "TenantConnection.RuntimeRead";
    ///     options.MigrationReadPolicy = "TenantConnection.MigrationRead";
    /// });
    /// </code>
    /// </example>
    /// <param name="endpoints">路由构建器（通常是宿主的路由组）。</param>
    /// <param name="configure">授权口径。</param>
    /// <returns>承载这组端点的路由组。</returns>
    public static RouteGroupBuilder MapTenantConnections(
        this IEndpointRouteBuilder endpoints,
        Action<TenantConnectionEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TenantConnectionEndpointOptions();
        configure(options);
        options.Validate();

        // 路由组上不挂默认策略：它通常要求自然人，会让机器令牌 403。组件不替宿主指定认证方案：
        // 具名策略省略方案时，由认证结果决定 Challenge 还是 Forbid，再由宿主默认方案回应；策略指定方案时用该方案。
        var group = endpoints.MapGroup(string.Empty);

        group.MapGet("{tenantId:guid}", (Guid tenantId, ITenantConnectionManagementService service, CancellationToken cancellationToken)
                => service.GetListAsync(tenantId, cancellationToken))
            .WithName(NamePrefix + "GetTenantConnections")
            .RequireAuthorization(options.ManagePolicy);

        group.MapPut("{tenantId:guid}/{name}", (
                Guid tenantId,
                string name,
                UpsertTenantConnectionInputDto input,
                ITenantConnectionManagementService service,
                CancellationToken cancellationToken)
                => service.SetAsync(tenantId, name, input, cancellationToken))
            .WithName(NamePrefix + "SetTenantConnection")
            .RequireAuthorization(options.ManagePolicy);

        group.MapDelete("{tenantId:guid}/{name}", async (
                Guid tenantId,
                string name,
                long expectedVersion,
                ITenantConnectionManagementService service,
                CancellationToken cancellationToken) =>
            {
                await service.RemoveAsync(tenantId, name, expectedVersion, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName(NamePrefix + "RemoveTenantConnection")
            .RequireAuthorization(options.ManagePolicy);

        if (!string.IsNullOrWhiteSpace(options.RuntimeReadPolicy))
        {
            group.MapGet("runtime/{tenantId:guid}", (
                    Guid tenantId,
                    string? name,
                    ITenantConnectionManagementService service,
                    CancellationToken cancellationToken)
                    => service.GetRuntimeAsync(tenantId, name ?? string.Empty, cancellationToken))
                .WithName(NamePrefix + "GetRuntimeTenantConnection")
                .RequireAuthorization(options.RuntimeReadPolicy);
        }

        if (!string.IsNullOrWhiteSpace(options.RuntimeReadPolicy))
        {
            // 不含连接串，因此与运行时路由同一档权限，常驻服务不必申请 DDL 身份。
            // activeOnly 必填、缺失即 400：它决定停用租户的库是否参与，不替调用方选数据范围。
            group.MapGet("databases", (
                    string? name,
                    bool activeOnly,
                    ITenantConnectionManagementService service,
                    CancellationToken cancellationToken)
                    => service.GetDatabaseListAsync(name ?? string.Empty, activeOnly, cancellationToken))
                .WithName(NamePrefix + "GetTenantDatabases")
                .RequireAuthorization(options.RuntimeReadPolicy);
        }

        if (!string.IsNullOrWhiteSpace(options.MigrationReadPolicy))
        {
            group.MapGet("migration", (string? name, ITenantConnectionManagementService service, CancellationToken cancellationToken)
                    => service.GetMigrationListAsync(name ?? string.Empty, cancellationToken))
                .WithName(NamePrefix + "GetMigrationTenantConnections")
                .RequireAuthorization(options.MigrationReadPolicy);
        }

        return group;
    }
}
