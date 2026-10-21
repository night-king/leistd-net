using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>路由约定：业务端点以 <c>/api/v1/</c> 开头（api.md §6）。</summary>
/// <remarks>
/// 判定在宿主实际映射出的端点上做，控制器与组件的 Minimal API 端点一起覆盖；
/// 协议与基础设施端点的路径由外部约定决定，列入带理由的白名单。
/// </remarks>
public sealed class RouteConventionTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    /// <summary>不受版本前缀约束的路由，每项写明理由；以 <c>/</c> 结尾的按前缀匹配，其余按整条路由匹配。</summary>
    private static readonly RouteExemption[] Exemptions =
    [
        new("/connect/", "OIDC 协议端点，路径由客户端配置与发现文档约定"),
        new("/hubs/", "实时连接端点，不是版本化的资源接口"),
        new("/api/health/", "部署探针，路径由编排配置引用"),
        new("/{*path:nonfile}", "SPA 回落：未命中接口与静态文件的路径交给前端路由"),
        new("/api", "SPA 回落前的接口兜底：未知接口路径答 404，不落到前端页面"),
        new("/api/{**path}", "同上，覆盖 /api 下的任意未知路径"),
    ];

    /// <summary>宿主映射的端点全部满足约定。</summary>
    [Fact]
    public void Mapped_endpoints_are_versioned_or_exempted()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        // 防止取错数据源后变成"零个端点、零个问题"的空转通过
        Assert.Contains(endpoints.OfType<RouteEndpoint>(), endpoint => endpoint.RoutePattern.RawText == "/api/health/live");
        Assert.Empty(FindViolations(endpoints, Exemptions));
    }

    /// <summary>合规、违规与白名单的端点各一：只有不带版本前缀且不在白名单的被报出。</summary>
    [Fact]
    public void Unversioned_routes_outside_the_exemptions_are_reported()
    {
        Endpoint[] endpoints =
        [
            Route("api/v1/orders/{id}"),
            Route("/api/orders"),
            Route("/hubs/orders"),
            Route("/api"),
        ];

        // 整条匹配的豁免只放过它自己："/api" 不能顺带放过 "/api/orders"
        var violation = Assert.Single(FindViolations(endpoints, [new("/hubs/", "夹具：实时连接"), new("/api", "夹具：接口兜底")]));

        Assert.Contains("/api/orders", violation);
    }

    private static List<string> FindViolations(IEnumerable<Endpoint> endpoints, IReadOnlyList<RouteExemption> exemptions) =>
        endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/'))
            .Where(path => !path.StartsWith("/api/v1/", StringComparison.Ordinal))
            .Where(path => !exemptions.Any(exemption => exemption.Covers(path)))
            .Distinct()
            .Select(path => $"{path}: route is not under /api/v1/ and has no exemption")
            .ToList();

    private static RouteEndpoint Route(string pattern) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), order: 0, EndpointMetadataCollection.Empty, pattern);

    private sealed record RouteExemption(string Route, string Reason)
    {
        public bool Covers(string path) => Route.EndsWith('/')
            ? path.StartsWith(Route, StringComparison.Ordinal)
            : path == Route;
    }
}
