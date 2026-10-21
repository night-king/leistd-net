using System.Net;
using System.Security.Claims;
using Leistd.MultiTenancy.AspNetCore;
using Leistd.MultiTenancy.Exceptions;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary>租户会话自恢复：会话所属租户不可用时注销并放行恢复，而不是把用户卡死在连登录页都打不开的状态。</summary>
public sealed class TenantSessionRecoveryTests
{
    [Fact]
    public async Task An_api_request_of_a_dead_tenant_session_is_signed_out_with_401()
    {
        using var host = await StartAsync(tenantClaim: true, failure: new TenantNotActiveException("acme"));

        var response = await host.GetTestClient().GetAsync("/api/data");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("X-Tenant-Invalid").Single());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("session=;", StringComparison.Ordinal));
    }

    /// <summary>只有后续身份已认证的会话同样按已认证处理：失效的租户会话被收回，而不是原样报错。</summary>
    [Fact]
    public async Task A_session_authenticated_only_by_a_later_identity_is_still_signed_out()
    {
        using var host = await StartAsync(tenantClaim: true, failure: new TenantNotActiveException("acme"), anonymousFirst: true);

        var response = await host.GetTestClient().GetAsync("/api/data");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_page_navigation_is_redirected_back_to_where_it_was()
    {
        using var host = await StartAsync(tenantClaim: true, failure: new TenantNotFoundException("acme"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/tenants?page=2");
        request.Headers.Accept.ParseAdd("text/html");

        var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/tenants?page=2", response.Headers.Location!.OriginalString);
    }

    // 判定"是不是租户会话"必须与解析链读同一个声明类型，否则改了租户 claim 名（ClaimTypeOptions.TenantId）的宿主永远恢复不了
    [Fact]
    public async Task A_custom_tenant_claim_type_is_recognized_as_a_tenant_session()
    {
        using var host = await StartAsync(tenantClaim: true, failure: new TenantNotActiveException("acme"), tenantClaimType: "org_id");

        var response = await host.GetTestClient().GetAsync("/api/data");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // 宿主会话没有租户可失效：原始错误照常抛出，不注销宿主管理员
    [Fact]
    public async Task A_host_session_keeps_the_original_error()
    {
        using var host = await StartAsync(tenantClaim: false, failure: new TenantNotFoundException("acme"));

        await Assert.ThrowsAsync<TenantNotFoundException>(() => host.GetTestClient().GetAsync("/api/data"));
    }

    // 租户声明非法时不是"某个租户的会话"：与解析链同一规则判定，保留原始错误，不替它注销
    [Fact]
    public async Task A_session_with_an_invalid_tenant_claim_keeps_the_original_error()
    {
        using var host = await StartAsync(tenantClaim: true, failure: new TenantNotFoundException("acme"), tenantClaimValue: "acme");

        await Assert.ThrowsAsync<TenantNotFoundException>(() => host.GetTestClient().GetAsync("/api/data"));
    }

    private static async Task<IHost> StartAsync(
        bool tenantClaim,
        Exception failure,
        string tenantClaimType = CustomClaimTypes.TenantId,
        string? tenantClaimValue = null,
        bool anonymousFirst = false)
        => await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services
                    .Configure<ClaimTypeOptions>(options => options.TenantId = tenantClaimType)
                    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie(options => options.Cookie.Name = "session"))
                .Configure(app =>
                {
                    // 认证替身：每个请求都是已登录会话，可选带租户声明
                    app.Use((context, next) =>
                    {
                        List<Claim> claims = [new(CustomClaimTypes.Subject, "u1")];
                        if (tenantClaim)
                        {
                            claims.Add(new Claim(tenantClaimType, tenantClaimValue ?? Guid.NewGuid().ToString()));
                        }

                        var authenticated = new ClaimsIdentity(claims, "Test");
                        context.User = anonymousFirst
                            ? new ClaimsPrincipal([new ClaimsIdentity(), authenticated])
                            : new ClaimsPrincipal(authenticated);
                        return next(context);
                    });
                    app.UseTenantSessionRecovery(options => options.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme);
                    app.Run(_ => throw failure);
                }))
            .StartAsync();
}
