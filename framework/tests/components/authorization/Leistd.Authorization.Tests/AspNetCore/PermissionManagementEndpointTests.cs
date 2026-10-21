using Leistd.Authorization.AspNetCore.Endpoints;
using Leistd.Authorization.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Authorization.Tests.AspNetCore;

/// <summary>权限管理端点：策略名必填，只为开放的主体类型映射授予端点，端点名带组件前缀。</summary>
public sealed class PermissionManagementEndpointTests
{
    [Theory]
    [InlineData(nameof(PermissionManagementEndpointOptions.CurrentPolicy))]
    [InlineData(nameof(PermissionManagementEndpointOptions.DefinitionsPolicy))]
    public void Mapping_without_a_policy_name_fails(string missing)
    {
        var error = Assert.Throws<ArgumentException>(() => new FakeEndpoints().MapPermissionManagement(options =>
        {
            if (missing != nameof(PermissionManagementEndpointOptions.CurrentPolicy))
            {
                options.CurrentPolicy = "App.CurrentUser";
            }
        }));

        Assert.Contains(missing, error.Message);
    }

    [Theory]
    [InlineData("Tenant", "App.Tenants")]
    [InlineData(PermissionGrantProviderNames.Role, " ")]
    public void An_unknown_subject_type_or_empty_grant_policy_fails(string providerName, string policy)
    {
        Assert.Throws<ArgumentException>(() => new FakeEndpoints().MapPermissionManagement(options =>
        {
            options.CurrentPolicy = "App.CurrentUser";
            options.DefinitionsPolicy = "App.Permissions";
            options.GrantPolicies[providerName] = policy;
        }));
    }

    [Fact]
    public void Only_the_opened_subject_types_get_grant_endpoints()
    {
        var endpoints = new FakeEndpoints();

        endpoints.MapPermissionManagement(options =>
        {
            options.CurrentPolicy = "App.CurrentUser";
            options.DefinitionsPolicy = "App.Permissions";
            options.GrantPolicies[PermissionGrantProviderNames.Role] = "App.Roles.ManagePermissions";
        });

        var routes = endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(endpoint => (endpoint.RoutePattern.RawText, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName))
            .ToList();
        Assert.Equal(
        [
            ("/current", PermissionManagementEndpoints.GetCurrentName),
            ("/definitions", PermissionManagementEndpoints.GetDefinitionsName),
            ("/grants/roles/{providerKey}", "Leistd.Authorization.GetRoleGrants"),
            ("/grants/roles/{providerKey}", "Leistd.Authorization.ReplaceRoleGrants"),
        ], routes);
    }

    /// <summary>每个端点只带自己那条具名策略。</summary>
    /// <remarks>
    /// 组上一句无参 <c>RequireAuthorization()</c> 会把宿主的默认策略额外叠到每个端点上：
    /// 具名策略通过了，端点仍可能因为宿主对"默认主体"的要求被拒，而映射处看不出来。
    /// </remarks>
    [Fact]
    public void No_endpoint_falls_back_to_the_host_default_policy()
    {
        var endpoints = new FakeEndpoints();

        endpoints.MapPermissionManagement(options =>
        {
            options.CurrentPolicy = "App.CurrentUser";
            options.DefinitionsPolicy = "App.Permissions";
            options.GrantPolicies[PermissionGrantProviderNames.Role] = "App.Roles.ManagePermissions";
        });

        var unnamed = endpoints.DataSources.SelectMany(source => source.Endpoints)
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Where(data => string.IsNullOrWhiteSpace(data.Policy))
                .Select(_ => endpoint.DisplayName))
            .ToList();

        Assert.Equal([], unnamed);
    }

    private sealed class FakeEndpoints : IEndpointRouteBuilder
    {
        // 用例服务已登记，端点参数才会被推断为服务而不是请求体
        public IServiceProvider ServiceProvider { get; } =
            new ServiceCollection().AddRouting().AddPermissionAuthorizationCore().BuildServiceProvider();

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
