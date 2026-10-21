using System.Security.Claims;
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
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary>多租户中间件端到端：解析链优先级、Store 校验失败语义、Change 覆盖下游管道。</summary>
public class MultiTenancyMiddlewareTests(MultiTenancyMiddlewareTests.HostFixture fixture)
    : IClassFixture<MultiTenancyMiddlewareTests.HostFixture>
{
    private static readonly Guid ActiveTenantId = Guid.NewGuid();
    private static readonly Guid InactiveTenantId = Guid.NewGuid();
    private static readonly Guid ClaimTenantId = ActiveTenantId;

    private readonly HttpClient _client = fixture.Client;

    private async Task<string> GetAsync(string path, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await _client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task No_hint_resolves_to_host()
    {
        Assert.Equal("host", await GetAsync("/"));
    }

    [Fact]
    public async Task Header_resolves_active_tenant_and_change_covers_pipeline()
    {
        var body = await GetAsync("/", ("X-Tenant", ActiveTenantId.ToString()));
        Assert.Equal(ActiveTenantId.ToString(), body);
    }

    [Fact]
    public async Task Header_resolves_by_name_case_insensitively()
    {
        var body = await GetAsync("/", ("X-Tenant", "AcMe"));
        Assert.Equal(ActiveTenantId.ToString(), body);
    }

    [Fact]
    public async Task Query_string_resolves_when_no_header()
    {
        var body = await GetAsync($"/?tenant={ActiveTenantId}");
        Assert.Equal(ActiveTenantId.ToString(), body);
    }

    /// <summary>已认证请求的未知/停用租户照常失败：只能探到自己的租户，明确报错对运维有价值。</summary>
    /// <remarks>
    /// 未配置全局异常处理器的 TestServer 会把异常原样抛给调用方；真实宿主由 Leistd.ExceptionHandling 映射。
    /// </remarks>
    [Fact]
    public async Task An_authenticated_request_still_fails_on_an_unknown_tenant()
    {
        // 已认证主体不采信请求头（防止改写宿主身份），租户只能来自 claim
        await Assert.ThrowsAsync<TenantNotFoundException>(
            () => GetAsync("/", ("X-Test-Auth", "u1"), ("X-Test-Tenant-Claim", Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task An_authenticated_request_still_fails_on_an_inactive_tenant()
    {
        await Assert.ThrowsAsync<TenantNotActiveException>(
            () => GetAsync("/", ("X-Test-Auth", "u1"), ("X-Test-Tenant-Claim", InactiveTenantId.ToString())));
    }

    /// <summary>未认证请求下，"不存在"与"已停用"必须给出同一种失败。</summary>
    /// <remarks>
    /// <para>两者不同就等于把租户的存在与启用状态告诉任何人：带上租户头打任意匿名端点，看状态码即可枚举。</para>
    /// <para>也不能放行继续走——那样请求会落到宿主上下文，租户用户输错租户名时凭据会拿去和宿主用户比对。</para>
    /// </remarks>
    [Fact]
    public async Task An_anonymous_request_cannot_tell_unknown_from_inactive()
    {
        var unknown = await Assert.ThrowsAsync<TenantNotFoundException>(
            () => GetAsync("/", ("X-Tenant", Guid.NewGuid().ToString())));
        var inactive = await Assert.ThrowsAsync<TenantNotFoundException>(
            () => GetAsync("/", ("X-Tenant", InactiveTenantId.ToString())));

        Assert.Equal(unknown.GetType(), inactive.GetType());
        Assert.Equal(unknown.Code, inactive.Code);
    }

    [Fact]
    public async Task Authenticated_claim_wins_over_header()
    {
        // 已登录用户的租户由 claim 定案，伪造头无法把请求挪进别的租户
        var body = await GetAsync("/",
            ("X-Test-Auth", "u1"),
            ("X-Test-Tenant-Claim", ClaimTenantId.ToString()),
            ("X-Tenant", Guid.NewGuid().ToString()));

        Assert.Equal(ClaimTenantId.ToString(), body);
    }

    [Fact]
    public async Task Authenticated_user_without_claim_is_host_even_with_header()
    {
        // 宿主用户（无 tenant_id claim）同样有定论：头不能把宿主会话改写成租户
        var body = await GetAsync("/",
            ("X-Test-Auth", "host-admin"),
            ("X-Tenant", ActiveTenantId.ToString()));

        Assert.Equal("host", body);
    }

    /// <summary>只有后续身份已认证的主体同样由 claim 定案租户，请求头改写不了。</summary>
    /// <remarks>
    /// 租户解析若只看第一个身份，这样的主体会被当成匿名，解析继续交给请求头——
    /// 而授权管线与当前用户都认为它已认证，于是它在一个由请求头选中的租户里被判权、被留痕。
    /// </remarks>
    [Fact]
    public async Task A_later_authenticated_identity_still_pins_the_tenant_to_its_claim()
    {
        var body = await GetAsync("/",
            ("X-Test-Auth", "u1"),
            ("X-Test-Anonymous-First", "1"),
            ("X-Test-Tenant-Claim", ClaimTenantId.ToString()),
            ("X-Tenant", Guid.NewGuid().ToString()));

        Assert.Equal(ClaimTenantId.ToString(), body);
    }

    /// <summary>这样的主体按已认证处理：停用的租户报"已停用"，而不是给匿名者的"不存在"。</summary>
    [Fact]
    public async Task A_later_authenticated_identity_is_told_the_tenant_is_inactive()
    {
        await Assert.ThrowsAsync<TenantNotActiveException>(() => GetAsync("/",
            ("X-Test-Auth", "u1"),
            ("X-Test-Anonymous-First", "1"),
            ("X-Test-Tenant-Claim", InactiveTenantId.ToString())));
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
                    .ConfigureServices(services =>
                    {
                        services.AddMultiTenancy();
                        services.AddInMemoryTenantStore(options =>
                        {
                            options.Tenants.Add(new TenantConfiguration
                            {
                                Id = ActiveTenantId,
                                Name = "acme",
                                NormalizedName = "ACME"
                            });
                            options.Tenants.Add(new TenantConfiguration
                            {
                                Id = InactiveTenantId,
                                Name = "frozen",
                                NormalizedName = "FROZEN",
                                IsActive = false
                            });
                        });
                    })
                    .Configure(app =>
                    {
                        // 模拟认证：请求头 X-Test-Auth 存在时构造已认证主体，
                        // X-Test-Tenant-Claim 存在时附 tenant_id claim
                        app.Use(async (context, next) =>
                        {
                            if (context.Request.Headers.TryGetValue("X-Test-Auth", out var user))
                            {
                                var claims = new List<Claim> { new("sub", user.ToString()) };
                                if (context.Request.Headers.TryGetValue("X-Test-Tenant-Claim", out var tenantClaim))
                                {
                                    claims.Add(new Claim(CustomClaimTypes.TenantId, tenantClaim.ToString()));
                                }

                                var authenticated = new ClaimsIdentity(claims, "Test");
                                // X-Test-Anonymous-First：已认证身份前面还有一个未认证的空身份
                                context.User = context.Request.Headers.ContainsKey("X-Test-Anonymous-First")
                                    ? new ClaimsPrincipal([new ClaimsIdentity(), authenticated])
                                    : new ClaimsPrincipal(authenticated);
                            }

                            await next(context);
                        });

                        app.UseMultiTenancy();

                        app.Run(async context =>
                        {
                            var currentTenant = context.RequestServices.GetRequiredService<ICurrentTenant>();
                            await context.Response.WriteAsync(currentTenant.Id?.ToString() ?? "host");
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
