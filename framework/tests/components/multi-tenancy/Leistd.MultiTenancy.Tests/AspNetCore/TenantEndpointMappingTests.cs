using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.AspNetCore.Endpoints;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary>租户管理与连接端点：策略名必填；机器端点只在配置了策略时映射；匿名探测端点显式放行。</summary>
public sealed class TenantEndpointMappingTests
{
    [Fact]
    public void Mapping_tenant_management_without_every_policy_fails()
    {
        var error = Assert.Throws<ArgumentException>(() => new FakeEndpoints().MapTenantManagement(options =>
        {
            options.ReadPolicy = "App.Tenants";
            options.CreatePolicy = "App.Tenants.Create";
            options.UpdatePolicy = "App.Tenants.Update";
        }));

        Assert.Contains(nameof(TenantManagementEndpointOptions.DeletePolicy), error.Message);
    }

    [Fact]
    public void Tenant_lookups_before_sign_in_are_anonymous_and_the_rest_require_policies()
    {
        var endpoints = new FakeEndpoints();
        endpoints.MapTenantManagement(options =>
        {
            options.ReadPolicy = "App.Tenants";
            options.CreatePolicy = "App.Tenants.Create";
            options.UpdatePolicy = "App.Tenants.Update";
            options.DeletePolicy = "App.Tenants.Delete";
        });

        var routes = endpoints.Routes();
        // 按名字查租户的匿名端点已移除（租户存在性 oracle），只剩按主机名探测这一个匿名端点
        Assert.Equal(7, routes.Count);
        Assert.Equal("/by-host", Assert.Single(routes, r => r.Anonymous).Pattern);
        Assert.All(routes.Where(r => r.Pattern != "/by-host"), r => Assert.False(r.Anonymous));
    }

    // 管理端点同理：组上一句无参 RequireAuthorization 会把宿主默认策略叠上去，
    // 具名策略通过了仍可能被默认策略拒，而映射处看不出来
    [Fact]
    public void Tenant_management_endpoints_carry_only_their_own_policy()
    {
        var endpoints = new FakeEndpoints();
        endpoints.MapTenantManagement(options =>
        {
            options.ReadPolicy = "App.Tenants";
            options.CreatePolicy = "App.Tenants.Create";
            options.UpdatePolicy = "App.Tenants.Update";
            options.DeletePolicy = "App.Tenants.Delete";
        });

        var unnamed = endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata
                .GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Any(data => string.IsNullOrWhiteSpace(data.Policy)))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Equal([], unnamed);
    }

    // 不签发机器令牌的部署里，永远无人可用的内部端点只是攻击面
    [Fact]
    public void Machine_connection_endpoints_are_mapped_only_with_their_policies()
    {
        var withoutMachines = new FakeEndpoints();
        withoutMachines.MapTenantConnections(options => options.ManagePolicy = "App.Tenants.Update");
        var withMachines = new FakeEndpoints();
        withMachines.MapTenantConnections(options =>
        {
            options.ManagePolicy = "App.Tenants.Update";
            options.RuntimeReadPolicy = "TenantConnection.RuntimeRead";
            options.MigrationReadPolicy = "TenantConnection.MigrationRead";
        });

        Assert.DoesNotContain(withoutMachines.Routes(), r => r.Pattern.Contains("runtime", StringComparison.Ordinal));
        Assert.Contains(withMachines.Routes(), r => r.Pattern == "/runtime/{tenantId:guid}");
        Assert.Contains(withMachines.Routes(), r => r.Pattern == "/migration");
    }

    // 宿主的默认策略通常要求自然人：叠在机器端点上，机器令牌永远 403
    [Fact]
    public void Machine_endpoints_carry_only_their_own_policy()
    {
        var endpoints = new FakeEndpoints();
        endpoints.MapTenantConnections(options =>
        {
            options.ManagePolicy = "App.Tenants.Update";
            options.RuntimeReadPolicy = "TenantConnection.RuntimeRead";
        });

        var runtime = endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/runtime/{tenantId:guid}");

        Assert.Equal(["TenantConnection.RuntimeRead"],
            runtime.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(a => a.Policy));
    }

    private sealed class FakeEndpoints : IEndpointRouteBuilder
    {
        // 用例服务已登记，端点参数才会被推断为服务而不是请求体
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection()
            .AddRouting()
            .AddLogging()
            .AddMultiTenancyEfCore<DbContext>()
            .AddTenantManagement()
            .BuildServiceProvider();

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);

        public List<(string Pattern, bool Anonymous)> Routes()
            => [.. DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Select(e => (e.RoutePattern.RawText!, e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null))];
    }
}
