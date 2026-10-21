using System.Security.Claims;
using Leistd.MultiTenancy.AspNetCore;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary>子域名解析：格式匹配规则，以及它在解析链中的位置。</summary>
public class DomainTenantResolveTests(DomainTenantResolveTests.HostFixture fixture)
    : IClassFixture<DomainTenantResolveTests.HostFixture>
{
    private static readonly Guid AcmeId = Guid.NewGuid();
    private static readonly Guid GlobexId = Guid.NewGuid();

    private readonly HttpClient _client = fixture.Client;

    /// <summary>
    /// 主机名到租户的映射规则，全部走真实请求——格式匹配是这个贡献者的对外契约，
    /// 不是可以只在内部函数上验的实现细节。
    /// </summary>
    [Theory]
    // 主机名大小写不敏感（RFC 4343）
    [InlineData("http://ACME.Example.COM/", "acme")]
    // 端口不参与匹配：格式只写主机名，开发（:5240）与生产共用一份配置
    [InlineData("http://acme.example.com:5240/", "acme")]
    // 恰好是基础域：宿主入口，不是某个租户
    [InlineData("http://example.com/", "host")]
    // 域名不属于本格式：不解析，交给链上后续贡献者
    [InlineData("http://acme.other.com/", "host")]
    // 多级子域不应被当成名为 "a.b" 的租户
    [InlineData("http://a.b.example.com/", "host")]
    public async Task Host_maps_to_a_tenant_only_when_it_fits_the_format(string url, string expected)
    {
        var body = await GetAsync(url);
        Assert.Equal(expected == "host" ? "host" : AcmeId.ToString(), body);
    }

    [Fact]
    public async Task Subdomain_resolves_the_tenant_without_any_header()
    {
        Assert.Equal(AcmeId.ToString(), await GetAsync("http://acme.example.com/"));
        Assert.Equal(GlobexId.ToString(), await GetAsync("http://globex.example.com/"));
    }

    /// <summary>子域名排在请求头之前：子域名部署下域名是权威，匿名请求不能用头把自己挪到别的租户。</summary>
    [Fact]
    public async Task Header_cannot_move_an_anonymous_request_off_the_subdomain()
    {
        var body = await GetAsync(
            "http://acme.example.com/",
            ("X-Tenant", GlobexId.ToString()));

        Assert.Equal(AcmeId.ToString(), body);
    }

    /// <summary>受管域内没解析出租户时必须就此定案为宿主，不能把决定权交还给请求头。</summary>
    /// <remarks>
    /// 只在成功提取到租户名时才写 <c>TenantIdOrName</c>、从不设 <c>Handled</c> 的话，
    /// 基础域与多级子域都会继续走到 Header 贡献者——匿名请求在 example.com 上带个
    /// 租户提示头就能挑任意租户，"域名是权威来源"这条契约当场失效。
    /// </remarks>
    [Theory]
    [InlineData("example.com")]             // 基础域：文档定义的宿主入口
    [InlineData("example.com.")]            // 同上的等价写法
    [InlineData("a.b.example.com")]         // 受管域内的多级子域：不是合法租户段
    public async Task Managed_domain_without_a_tenant_label_stays_on_host(string hostHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        request.Headers.TryAddWithoutValidation("Host", hostHeader);
        request.Headers.TryAddWithoutValidation("X-Tenant", GlobexId.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal("host", await response.Content.ReadAsStringAsync());
    }

    /// <summary>前缀形态下，受管域内但不匹配前缀的主机同样定案为宿主，不回退请求头。</summary>
    /// <remarks>
    /// <c>tenant-{0}.example.com</c> 部署里的 <c>other.example.com</c> 属于同一个域、
    /// 但不是租户入口；它若能被请求头改写，前缀形态就白设了。
    /// </remarks>
    [Fact]
    public async Task Prefix_format_rejects_the_header_inside_the_managed_domain()
    {
        using var host = await new HostBuilder().ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddMultiTenancy(options => options.DomainFormat = "tenant-{0}.example.com");
                services.AddInMemoryTenantStore(options => options.Tenants.Add(new TenantConfiguration
                {
                    Id = GlobexId,
                    Name = "globex",
                    NormalizedName = "GLOBEX"
                }));
            })
            .Configure(app =>
            {
                app.UseMultiTenancy();
                app.Run(async context =>
                {
                    var currentTenant = context.RequestServices.GetRequiredService<ICurrentTenant>();
                    await context.Response.WriteAsync(currentTenant.Id?.ToString() ?? "host");
                });
            })).StartAsync();

        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        request.Headers.TryAddWithoutValidation("Host", "other.example.com");
        request.Headers.TryAddWithoutValidation("X-Tenant", GlobexId.ToString());

        var response = await client.SendAsync(request);
        Assert.Equal("host", await response.Content.ReadAsStringAsync());

        await host.StopAsync();
    }

    /// <summary>受管域按 DNS label 边界算，不是占位符之后的原始字符串。</summary>
    /// <remarks>
    /// <c>{0}-tenant.example.com</c> 的受管域应是 <c>example.com</c>。若按原始字符串取后缀，
    /// 后缀会变成 <c>-tenant.example.com</c>，于是基础域被判为"域外"而回退请求头——
    /// 合法格式下安全边界没有闭合。
    /// </remarks>
    [Theory]
    [InlineData("acme-tenant.example.com", "tenant")]   // 合法租户段
    [InlineData("example.com", "host")]                 // 基础域：定案为宿主
    [InlineData("other.example.com", "host")]           // 同域但不匹配段内后缀
    public async Task Managed_domain_is_computed_on_label_boundaries(string hostHeader, string expected)
    {
        using var host = await new HostBuilder().ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddMultiTenancy(options => options.DomainFormat = "{0}-tenant.example.com");
                services.AddInMemoryTenantStore(options => options.Tenants.Add(new TenantConfiguration
                {
                    Id = AcmeId,
                    Name = "acme",
                    NormalizedName = "ACME"
                }));
            })
            .Configure(app =>
            {
                app.UseMultiTenancy();
                app.Run(async context =>
                {
                    var currentTenant = context.RequestServices.GetRequiredService<ICurrentTenant>();
                    await context.Response.WriteAsync(currentTenant.Id?.ToString() ?? "host");
                });
            })).StartAsync();

        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        request.Headers.TryAddWithoutValidation("Host", hostHeader);
        request.Headers.TryAddWithoutValidation("X-Tenant", GlobexId.ToString());

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected == "host" ? "host" : AcmeId.ToString(), body);

        await host.StopAsync();
    }

    /// <summary>
    /// 受管域之外的请求仍走请求头：服务间调用打的是集群内部主机名，
    /// 租户靠提示头传递，一刀切会把它打断。
    /// </summary>
    [Fact]
    public async Task Requests_outside_the_managed_domain_still_use_the_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://acme.other.com/");
        request.Headers.TryAddWithoutValidation("Host", "svc.internal.cluster.local");
        request.Headers.TryAddWithoutValidation("X-Tenant", GlobexId.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(GlobexId.ToString(), await response.Content.ReadAsStringAsync());
    }

    /// <summary>末尾根点是 DNS 中的等价写法，不能成为绕过子域名权威的后门。</summary>
    /// <remarks>
    /// <c>acme.example.com.</c> 与 <c>acme.example.com</c> 在 DNS 中是同一个名字，
    /// 而 <c>HostString.Host</c> 会原样保留那个点。字面比较匹配不上就会退回请求头，
    /// 于是匿名请求只要在 Host 末尾多打一个点，就能用租户提示头挑任意租户。
    /// 配置侧要求不带根点（唯一 canonical 形态），请求侧则必须规范化后再匹配。
    /// </remarks>
    [Theory]
    [InlineData("acme.example.com.")]
    [InlineData("acme.example.com.:5240")]
    // 去掉的是全部末尾点而非恰好一个：只去一个的话，多打一个点绕过又回来了
    [InlineData("acme.example.com..")]
    public async Task Trailing_root_dot_in_the_host_is_not_a_bypass(string hostHeader)
    {
        // 必须显式写 Host 头：HttpClient 在构造 URI 时就会把末尾点规范化掉，
        // 用带点的 URL 根本打不到这条路径——而攻击者是在报文里直接写这个头的
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://acme.example.com/");
        // 不走校验的 setter：攻击者是在报文里直接写这个头的，
        // HttpRequestHeaders.Host 的校验只是客户端的礼貌，不是服务端的保证
        request.Headers.TryAddWithoutValidation("Host", hostHeader);
        request.Headers.TryAddWithoutValidation("X-Tenant", GlobexId.ToString());

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(AcmeId.ToString(), body);
    }

    /// <summary>claim 仍然优先于子域名：已认证主体的租户由 claim 定案，这条顺序是防跨租户越权的关键。</summary>
    [Fact]
    public async Task Authenticated_principal_claim_still_wins_over_the_subdomain()
    {
        var body = await GetAsync(
            "http://acme.example.com/",
            ("X-Test-Auth", "user-1"),
            ("X-Test-Tenant-Claim", GlobexId.ToString()));

        Assert.Equal(GlobexId.ToString(), body);
    }

    /// <summary>非法 DomainFormat 必须在启动期失败，而不是运行期静默退回请求头解析。</summary>
    /// <remarks>
    /// 这是 fail-open 的典型形态：配置写错了，系统照常启动、看起来在跑，
    /// 而"子域名是权威来源"这条边界已经没了——请求头重新说了算。
    /// </remarks>
    [Theory]
    [InlineData("example.com")]                 // 漏了占位符
    [InlineData("{0}.{0}.example.com")]         // 多个占位符
    [InlineData("https://{0}.example.com")]     // 带 scheme
    [InlineData("{0}.example.com/app")]         // 带路径
    [InlineData("{0}.example.com:5240")]        // 带端口
    [InlineData("{0}. example.com")]            // 含空白
    [InlineData("{0}.example.com?source=x")]    // 带查询串
    [InlineData("{0}.example.com#fragment")]    // 带片段
    [InlineData("user@{0}.example.com")]        // 带用户信息
    [InlineData("{0}..example.com")]            // 空 label
    [InlineData("{0}.{1}.example.com")]         // 混入其它占位符
    [InlineData("{0}\\example.com")]            // 反斜杠
    [InlineData("{0}_.example.com")]            // 下划线：CheckHostName 放行，浏览器不会这么发
    [InlineData("{0}-.example.com")]            // 段尾连字符
    [InlineData("{0}.example-.com")]            // 同上，出现在后缀段
    [InlineData("{0}.bücher.example")]          // Unicode：应要求填 punycode
    [InlineData("{0}")]                         // 只有一段
    [InlineData("example.{0}")]                 // 占位符在末段：没有固定基础域
    [InlineData("{0}.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.com")]  // 段超 63
    public async Task Invalid_domain_format_stops_the_host_from_starting(string format)
    {
        var builder = new HostBuilder().ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddMultiTenancy(options => options.DomainFormat = format);
                services.AddInMemoryTenantStore(_ => { });
            })
            .Configure(app => app.UseMultiTenancy()));

        var error = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            using var host = await builder.StartAsync();
        });

        Assert.Contains("DomainFormat", error.Message, StringComparison.Ordinal);
    }

    /// <summary>占位符不必占满整个 label：<c>tenant-{0}.example.com</c> 是合法形态。</summary>
    /// <remarks>
    /// 替换后仍是合法主机名即可；租户段本身不含点号由运行期解析保证。
    /// </remarks>
    [Theory]
    [InlineData("{0}.example.com")]
    [InlineData("tenant-{0}.example.com")]
    [InlineData("{0}.app.example.com")]
    [InlineData("{0}.xn--bcher-kva.example")]   // punycode 形态：与浏览器发送的 Host 一致
    public async Task Well_formed_domain_formats_start_normally(string format)
    {
        using var host = await new HostBuilder().ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddMultiTenancy(options => options.DomainFormat = format);
                services.AddInMemoryTenantStore(_ => { });
            })
            .Configure(app => app.UseMultiTenancy())).StartAsync();

        await host.StopAsync();
    }

    [Fact]
    public async Task Absent_domain_format_is_valid_and_simply_skips_the_contributor()
    {
        using var host = await new HostBuilder().ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddMultiTenancy();
                services.AddInMemoryTenantStore(_ => { });
            })
            .Configure(app => app.UseMultiTenancy())).StartAsync();

        await host.StopAsync();
    }

    private async Task<string> GetAsync(string url, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
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
                    .ConfigureServices(services =>
                    {
                        services.AddMultiTenancy(options => options.DomainFormat = "{0}.example.com");
                        services.AddInMemoryTenantStore(options =>
                        {
                            options.Tenants.Add(new TenantConfiguration
                            {
                                Id = AcmeId,
                                Name = "acme",
                                NormalizedName = "ACME"
                            });
                            options.Tenants.Add(new TenantConfiguration
                            {
                                Id = GlobexId,
                                Name = "globex",
                                NormalizedName = "GLOBEX"
                            });
                        });
                    })
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            if (context.Request.Headers.TryGetValue("X-Test-Auth", out var user))
                            {
                                var claims = new List<Claim> { new("sub", user.ToString()) };
                                if (context.Request.Headers.TryGetValue("X-Test-Tenant-Claim", out var tenantClaim))
                                {
                                    claims.Add(new Claim(CustomClaimTypes.TenantId, tenantClaim.ToString()));
                                }

                                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
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
