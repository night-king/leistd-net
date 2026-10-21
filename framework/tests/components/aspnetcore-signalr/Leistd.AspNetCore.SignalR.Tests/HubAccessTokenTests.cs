using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Leistd.AspNetCore.SignalR.Tests.TestDoubles;

namespace Leistd.AspNetCore.SignalR.Tests;

/// <summary>查询串令牌只在 Hub 端点上转成 Bearer 头，其它端点照旧拒绝。</summary>
/// <remarks>
/// 浏览器的 WebSocket 与 SSE 不能带自定义头，令牌只能走查询串；但在普通端点上接受查询串令牌，
/// 会让令牌出现在访问日志与 Referer 里。识别按端点元数据，与 Hub 挂在哪个路径无关。
/// </remarks>
public sealed class HubAccessTokenTests(HubAccessTokenTests.HostFixture fixture)
    : IClassFixture<HubAccessTokenTests.HostFixture>
{
    private readonly HttpClient _client = fixture.Client;

    private async Task<string> ProbeAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Probe", "1");
        return await (await _client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_hub_request_moves_the_query_token_into_the_authorization_header()
    {
        Assert.Equal("Bearer abc|?negotiateVersion=1", await ProbeAsync("/realtime/custom-path/negotiate?access_token=abc&negotiateVersion=1"));
    }

    [Fact]
    public async Task Other_endpoints_keep_rejecting_query_tokens()
    {
        Assert.Equal("|?access_token=abc", await ProbeAsync("/api/orders?access_token=abc"));
    }

    /// <summary>本类用例共享的宿主：配置固定、用例之间没有逐测可变的宿主状态。</summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        public IHost Host { get; private set; } = default!;

        public HttpClient Client { get; private set; } = default!;

        public async Task InitializeAsync()
        {
            Host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services => services.AddRouting().AddSignalR())
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseHubAccessToken();
                        // 在认证本该发生的位置回显请求头与查询串，断言转换结果
                        app.Use(async (context, next) =>
                        {
                            if (context.Request.Headers.ContainsKey("X-Probe"))
                            {
                                await context.Response.WriteAsync($"{context.Request.Headers.Authorization}|{context.Request.QueryString}");
                                return;
                            }

                            await next(context);
                        });
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHub<TestHub>("/realtime/custom-path");
                            endpoints.MapGet("/api/orders", () => "ok");
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
