using System.Security.Claims;
using Leistd.MultiTenancy.AspNetCore.Resolution;
using Leistd.MultiTenancy.AspNetCore;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.Context;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary>资源服务不查注册表，租户上下文只来自已验证主体的声明。</summary>
/// <remarks>
/// <c>ValidateResolvedTenant = false</c> 复用同一中间件：有租户声明进入租户上下文，无声明进入宿主上下文。
/// 边界仍由 <c>MultiTenancySides</c> 与全局过滤器约束；宿主上下文只见 <c>TenantId IS NULL</c>，跨租户操作须有 Host 侧权限。
/// </remarks>
public class ResourceServiceTenantContextTests(ResourceServiceTenantContextTests.HostFixture fixture)
    : IClassFixture<ResourceServiceTenantContextTests.HostFixture>
{
    private readonly HttpClient _client = fixture.Client;

    [Fact]
    public async Task Verified_claim_establishes_the_tenant_without_a_store()
    {
        var tenantId = Guid.NewGuid();

        var response = await SendAsync("/orders", subject: "alice", tenantClaims: [tenantId.ToString()]);

        Assert.Equal(tenantId.ToString(), response);
    }

    /// <summary>已认证但无租户 claim：宿主上下文，不是错误。</summary>
    /// <remarks>
    /// 机器主体（客户端凭据）、宿主用户、平台运维端点都落在这里；若一律 401，
    /// 控制面接口只能标成公开或搬去 Identity 服务。
    /// </remarks>
    [Fact]
    public async Task Authenticated_principal_without_a_tenant_claim_runs_as_host()
    {
        var response = await SendAsync("/metrics", subject: "platform-operator", tenantClaims: []);

        Assert.Equal("host", response);
    }

    [Fact]
    public async Task Anonymous_request_runs_as_host()
    {
        Assert.Equal("host", await SendAsync("/health", subject: null, tenantClaims: []));
    }

    // 必须用匿名请求验证请求头、查询串和子域名被忽略；已认证请求会在主体贡献者处 Handled，测不到后续链收窄。
    // 跳过注册表校验须同时收窄解析链，避免匿名端点使用调用方指定的租户。
    [Theory]
    [InlineData("http://localhost/orders?tenant=11111111-1111-1111-1111-111111111111", null)]
    [InlineData("http://localhost/orders", "X-Tenant")]
    [InlineData("http://acme.example.com/orders", null)]
    public async Task Unverified_tenant_hints_from_anonymous_requests_are_ignored(
        string url,
        string? headerName)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headerName is not null)
        {
            request.Headers.Add(headerName, Guid.NewGuid().ToString());
        }

        var response = await _client.SendAsync(request);

        Assert.Equal("host", await response.Content.ReadAsStringAsync());
    }

    /// <summary>已认证请求的租户由 claim 定案，请求头无法改写。</summary>
    /// <remarks>
    /// 这条与上一条防的是不同的东西：本条靠贡献者顺序（主体在链首且终止链），
    /// 上一条靠链收窄。两道都要有——前者管已认证请求，后者管匿名请求。
    /// </remarks>
    [Fact]
    public async Task Header_cannot_override_a_verified_claim()
    {
        var claimTenant = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/orders");
        request.Headers.Add("X-Test-Auth", "alice");
        request.Headers.Add("X-Test-Tenant-Claim", claimTenant.ToString());
        request.Headers.Add("X-Tenant", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(claimTenant.ToString(), await response.Content.ReadAsStringAsync());
    }

    /// <summary>claim 格式非法时拒绝，而不是静默退回宿主。</summary>
    /// <remarks>
    /// 令牌里带着一个解析不出的 <c>tenant_id</c> 是签发方或令牌被篡改的信号。
    /// 退回宿主意味着一个本应受租户约束的请求悄悄获得了宿主视角——按失败关闭处理。
    /// </remarks>
    [Fact]
    public async Task Malformed_tenant_claim_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidTenantClaimException>(
            () => SendAsync("/orders", subject: "alice", tenantClaims: ["not-a-guid"]));
    }

    // 形状合法、租户却不存在：由注册表校验拒绝
    [Fact]
    public async Task A_well_formed_claim_of_an_unknown_tenant_is_rejected()
    {
        await Assert.ThrowsAsync<TenantNotFoundException>(
            () => SendAsync("/orders", subject: "alice", tenantClaims: ["00000000-0000-0000-0000-000000000000"]));
    }

    /// <summary>多条租户 claim 一律失败关闭，即使两个值完全相同。</summary>
    /// <remarks>
    /// <para>若用 <c>FindFirst()</c>，两条 claim 会静默取第一条。那是在安全边界上做静默选择——
    /// 攻击者只要能让令牌多出一条，就能决定后续所有租户过滤器、权限检查和写入落值的归属。</para>
    /// <para>值相同的那一档也必须拒绝。"反正结果一样所以无害"是拿当前实现的巧合当保证：
    /// 一旦哪天取的不是第一条，行为就变了；而且它会掩盖签发侧真实存在的缺陷——
    /// 一个主体带两条租户 claim，只可能来自签发方出错或令牌被拼接/篡改。</para>
    /// </remarks>
    [Fact]
    public async Task Duplicate_tenant_claims_with_different_values_are_rejected()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        var rejected = await Assert.ThrowsAsync<InvalidTenantClaimException>(
            () => SendAsync("/orders", subject: "alice", tenantClaims: [first, second]));

        Assert.Equal(CustomClaimTypes.TenantId, rejected.ClaimType);
    }

    [Fact]
    public async Task Duplicate_tenant_claims_with_identical_values_are_also_rejected()
    {
        var tenantId = Guid.NewGuid().ToString();

        var rejected = await Assert.ThrowsAsync<InvalidTenantClaimException>(
            () => SendAsync("/orders", subject: "alice", tenantClaims: [tenantId, tenantId]));

        Assert.Equal(CustomClaimTypes.TenantId, rejected.ClaimType);
    }

    private async Task<string> SendAsync(string path, string? subject, string[] tenantClaims)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (subject is not null)
        {
            request.Headers.Add("X-Test-Auth", subject);
        }

        foreach (var claim in tenantClaims)
        {
            request.Headers.Add("X-Test-Tenant-Claim", claim);
        }

        var response = await _client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>本类用例共享的宿主：配置固定、用例之间没有逐测可变的宿主状态。</summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        public IHost Host { get; private set; } = default!;

        public HttpClient Client { get; private set; } = default!;

        public async Task InitializeAsync()
        {
            Host = await new HostBuilder()
                .ConfigureWebHost(builder => builder
                    .UseTestServer()
                    // 刻意不注册任何 ITenantStore：资源服务没有注册表，
                    // 若中间件在本形态下仍去查存储，这里会直接抛出来
                    .ConfigureServices(services => services.AddMultiTenancy(options =>
                    {
                        options.ValidateResolvedTenant = false;
                        // 即便配了子域名格式也不该生效——链已被收窄
                        options.DomainFormat = "{0}.example.com";
                    }))
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            if (context.Request.Headers.TryGetValue("X-Test-Auth", out var subject))
                            {
                                var claims = new List<Claim> { new("sub", subject.ToString()) };
                                foreach (var value in context.Request.Headers["X-Test-Tenant-Claim"])
                                {
                                    claims.Add(new Claim(CustomClaimTypes.TenantId, value!));
                                }

                                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
                            }

                            await next(context);
                        });

                        app.UseMultiTenancy();
                        app.Run(async context =>
                        {
                            var tenant = context.RequestServices.GetRequiredService<ICurrentTenant>();
                            await context.Response.WriteAsync(tenant.Id?.ToString() ?? "host");
                        });
                    }))
                .StartAsync();

            Client = Host.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}
