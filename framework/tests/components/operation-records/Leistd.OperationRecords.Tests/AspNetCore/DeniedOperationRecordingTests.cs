using System.Security.Claims;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.AspNetCore.Attributes;
using Leistd.OperationRecords.AspNetCore.Extensions;
using Leistd.OperationRecords.Tests.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.OperationRecords.Tests.AspNetCore;

/// <summary>授权阶段的拒绝：注解决定记不记，框架不再二次筛选。</summary>
/// <remarks>
/// <c>[Authorize(Policy = ...)]</c> 的拒绝发生在授权阶段，请求到不了应用服务，
/// 那里的记录调用看不见它——于是"谁在反复尝试他没有的权限"这类问题没有任何痕迹可查。
/// </remarks>
public sealed class DeniedOperationRecordingTests
{
    private static (HttpContext Context, RecordingOperationRecordStore Store) Create(
        string method = "PUT",
        bool authenticated = true,
        object[]? metadata = null,
        Dictionary<string, object?>? routeValues = null,
        Claim[]? claims = null)
    {
        var store = new RecordingOperationRecordStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("App.Orders.Update", policy => policy.RequireClaim("permission", "App.Orders.Update"));
            options.AddPolicy("Security.RecentMfa", policy => policy.RequireClaim("amr", "mfa"));
        });
        services.AddSingleton<IOperationRecordWriter>(store);
        services.AddScoped<RecordedFailureTracker>();
        services.AddTransient<IOperationRecorder>(
            provider => new PassThroughRecorder(store, provider.GetRequiredService<RecordedFailureTracker>()));

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            // 认证与否只看 ClaimsIdentity 有没有 authenticationType，与有没有 claim 无关
            User = authenticated
                ? new ClaimsPrincipal(new ClaimsIdentity(claims ?? [], "TestBearer"))
                : new ClaimsPrincipal(new ClaimsIdentity())
        };
        context.Request.Method = method;

        if (metadata is not null)
        {
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        }

        foreach (var (key, value) in routeValues ?? new())
        {
            context.Request.RouteValues[key] = value;
        }

        return (context, store);
    }

    [Fact]
    public async Task An_endpoint_with_the_attribute_is_recorded()
    {
        var (context, store) = Create(metadata:
        [
            new AuthorizeAttribute { Policy = "App.Roles.Update" },
            new OperationRecordActionAttribute("identity.role.updated", "id")
        ], routeValues: new() { ["id"] = "r-1" });

        await context.RecordDeniedOperationAsync();

        var written = Assert.Single(store.Written);
        Assert.Equal("identity.role.updated", written.Action);
        Assert.Equal("r-1", written.TargetId);
        Assert.Equal("App.Roles.Update", written.AuthorizationBasis);
        Assert.Equal(OperationRecordOutcome.Failed, written.Outcome);
    }

    /// <summary>不传失败原因时记通用的被拒码：没有码的失败记录事后无法按原因聚合。</summary>
    [Fact]
    public async Task Without_a_failure_the_generic_forbidden_code_is_recorded()
    {
        var (context, store) = Create(metadata:
        [
            new AuthorizeAttribute { Policy = "App.Roles.Update" },
            new OperationRecordActionAttribute("identity.role.updated", "id")
        ]);

        await context.RecordDeniedOperationAsync();

        var written = Assert.Single(store.Written);
        Assert.Equal("Error:Forbidden", written.FailureCode);
        Assert.Null(written.FailureDetail);
    }

    /// <summary>调用方给了码就用它，不覆盖。</summary>
    [Fact]
    public async Task A_caller_supplied_code_is_kept()
    {
        var (context, store) = Create(metadata:
        [
            new AuthorizeAttribute { Policy = "App.Roles.Update" },
            new OperationRecordActionAttribute("identity.role.updated", "id")
        ]);

        await context.RecordDeniedOperationAsync(OperationFailure.FromCode(
            "Role:Protected",
            new Dictionary<string, object?> { ["name"] = "admin" }));

        var written = Assert.Single(store.Written);
        Assert.Equal("Role:Protected", written.FailureCode);
        Assert.Equal("""{"name":"admin"}""", written.FailureData);
    }

    /// <summary>只给了 Detail 同样算给过原因，不能被换成通用 Forbidden。</summary>
    /// <remarks>
    /// 判据若写成 <c>failure.Code is null</c>，由于 <c>FromDetail</c> 给出的原因本来就没有码，
    /// 调用方显式传入的 Detail 会被静默丢弃。
    /// </remarks>
    [Fact]
    public async Task A_caller_supplied_detail_without_a_code_is_kept()
    {
        var (context, store) = Create(metadata:
        [
            new AuthorizeAttribute { Policy = "App.Roles.Update" },
            new OperationRecordActionAttribute("identity.role.updated", "id")
        ]);

        await context.RecordDeniedOperationAsync(OperationFailure.FromDetail("scope mismatch"));

        var written = Assert.Single(store.Written);
        Assert.Equal("scope mismatch", written.FailureDetail);
        Assert.Null(written.FailureCode);
    }

    /// <summary>没有注解就不记：注解是唯一的开关。</summary>
    [Fact]
    public async Task An_endpoint_without_the_attribute_is_not_recorded()
    {
        var (context, store) = Create(metadata: [new AuthorizeAttribute { Policy = "App.Roles.Update" }]);

        await context.RecordDeniedOperationAsync();

        Assert.Empty(store.Written);
    }

    /// <summary>注解在哪就记哪，不按 HTTP 方法二次否决。</summary>
    /// <remarks>
    /// 不按 HTTP 方法再筛一道（如只记 POST/PUT/PATCH/DELETE）。开发者把注解打在敏感的 <c>GET</c>
    /// 导出端点上，已经明确表达了"这个动作值得留痕"，框架再筛一道会让它静默失效——
    /// 而失效的表现是"审计里什么都没有"，没有任何报错提示。
    /// </remarks>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task The_http_method_does_not_override_the_attribute(string method)
    {
        var (context, store) = Create(method: method, metadata:
        [
            new OperationRecordActionAttribute("data.export.requested")
        ]);

        await context.RecordDeniedOperationAsync();

        Assert.Single(store.Written);
    }

    /// <summary>匿名请求一律不记。</summary>
    /// <remarks>
    /// 这不是偏好而是安全属性：匿名请求没有操作人，记下来等于把审计表变成一个
    /// 不需要凭据的写入面，任何人都能往里灌数据。
    /// </remarks>
    [Fact]
    public async Task An_anonymous_request_is_never_recorded()
    {
        var (context, store) = Create(authenticated: false, metadata:
        [
            new OperationRecordActionAttribute("identity.role.updated", "id")
        ], routeValues: new() { ["id"] = "r-1" });

        await context.RecordDeniedOperationAsync();

        Assert.Empty(store.Written);
    }

    /// <summary>任一身份已认证即不算匿名，与官方 <c>DenyAnonymousAuthorizationRequirement</c> 一致。</summary>
    /// <remarks>若只看 <c>User.Identity</c>（第一个身份），首身份未认证时整条记录会被当成匿名丢掉。</remarks>
    [Fact]
    public async Task A_principal_authenticated_only_by_a_later_identity_is_recorded()
    {
        var (context, store) = Create(metadata:
        [
            new OperationRecordActionAttribute("identity.role.updated")
        ]);
        context.User = new ClaimsPrincipal([new ClaimsIdentity(), new ClaimsIdentity([], "TestBearer")]);

        await context.RecordDeniedOperationAsync();

        Assert.Single(store.Written);
    }

    /// <summary>目标标识按声明顺序取多个路由值拼接。</summary>
    /// <remarks>
    /// 目标标识不总是一个值：按 <c>(租户, 连接名)</c> 逐行登记的资源，标识是 <c>{tenantId}/{name}</c>。
    /// 只取第一段会让被拒记录与成功路径的写法分叉，按目标检索就只能查到一半。
    /// </remarks>
    [Fact]
    public async Task Multiple_route_keys_are_joined_in_declaration_order()
    {
        var (context, store) = Create(metadata:
        [
            new OperationRecordActionAttribute("tenant-connection.updated", "tenantId", "name")
        ], routeValues: new() { ["tenantId"] = "t-1", ["name"] = "crm" });

        await context.RecordDeniedOperationAsync();

        Assert.Equal("t-1/crm", Assert.Single(store.Written).TargetId);
    }

    /// <summary>缺任何一段就整体记为占位值，不记半截。</summary>
    /// <remarks>半截的标识既检索不到成功路径写下的那条，又看起来像一个真实存在的目标。</remarks>
    [Fact]
    public async Task A_missing_route_value_yields_the_placeholder_rather_than_half_an_identifier()
    {
        var (context, store) = Create(metadata:
        [
            new OperationRecordActionAttribute("tenant-connection.updated", "tenantId", "name")
        ], routeValues: new() { ["tenantId"] = "t-1" });

        await context.RecordDeniedOperationAsync();

        Assert.Equal("-", Assert.Single(store.Written).TargetId);
    }

    /// <summary>取动作上的策略名，而不是类级 <c>[Authorize]</c> 的空策略。</summary>
    [Fact]
    public async Task The_policy_name_comes_from_the_action_not_the_controller()
    {
        var (context, store) = Create(metadata:
        [
            new AuthorizeAttribute(),                                   // 类级：Policy 为空
            new AuthorizeAttribute { Policy = "App.Roles.Update" },     // 动作级：真正的策略
            new OperationRecordActionAttribute("identity.role.updated")
        ]);

        await context.RecordDeniedOperationAsync();

        Assert.Equal("App.Roles.Update", Assert.Single(store.Written).AuthorizationBasis);
    }

    /// <summary>叠了多个策略时，授权依据取实际没通过的那个，而不是书写顺序上的最后一个。</summary>
    /// <remarks>
    /// 若取最后一个具名策略，权限策略之后再叠一个近期 MFA 策略时，
    /// 被权限拒绝记下的却是 MFA，业务只能靠调整特性顺序规避，而那只是换了一种情况记错。
    /// </remarks>
    [Theory]
    [InlineData("amr", "mfa", "App.Orders.Update")]
    [InlineData("permission", "App.Orders.Update", "Security.RecentMfa")]
    public async Task The_basis_is_the_policy_that_actually_failed(
        string claimType, string claimValue, string expectedBasis)
    {
        var (context, store) = Create(
            metadata:
            [
                new AuthorizeAttribute { Policy = "App.Orders.Update" },
                new AuthorizeAttribute { Policy = "Security.RecentMfa" },
                new OperationRecordActionAttribute("orders.updated")
            ],
            claims: [new Claim(claimType, claimValue)]);

        await context.RecordDeniedOperationAsync();

        Assert.Equal(expectedBasis, Assert.Single(store.Written).AuthorizationBasis);
    }
}
