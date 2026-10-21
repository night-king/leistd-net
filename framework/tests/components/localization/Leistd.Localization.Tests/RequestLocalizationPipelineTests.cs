using System.Globalization;
using Leistd.Localization.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Leistd.Localization.Tests;

/// <summary>
/// <c>UseJsonRequestLocalization</c> 经真实管道把请求区域性解析为 <c>SupportedCultures</c> 中的一项：
/// 无偏好或偏好不受支持时回落到首项，解析结果同时决定本地化文案与响应的 <c>Content-Language</c>。
/// </summary>
public sealed class RequestLocalizationPipelineTests(RequestLocalizationPipelineTests.HostFixture fixture)
    : IClassFixture<RequestLocalizationPipelineTests.HostFixture>
{
    [Theory]
    [InlineData(null, null, "zh-CN", "未找到")]
    [InlineData("en", null, "en", "Not Found")]
    [InlineData("en-US,en;q=0.9", null, "en", "Not Found")]
    [InlineData("fr", null, "zh-CN", "未找到")]
    [InlineData("en", "zh-CN", "zh-CN", "未找到")]
    public async Task Request_culture_comes_from_supported_cultures_with_the_first_as_fallback(
        string? acceptLanguage, string? queryCulture, string expectedCulture, string expectedText)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, queryCulture is null ? "/" : $"/?culture={queryCulture}");
        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        using var response = await fixture.Client.SendAsync(request);
        var body = (await response.Content.ReadAsStringAsync()).Split('|');

        Assert.Equal(expectedCulture, body[0]);
        Assert.Equal(expectedCulture, body[1]);
        Assert.Equal(expectedText, body[2]);
        Assert.Equal([expectedCulture], response.Content.Headers.ContentLanguage);
    }

    // 注册入口只登记服务：不调用中间件时请求偏好不生效，响应也不带 Content-Language。
    [Fact]
    public async Task Without_the_middleware_the_request_preference_is_ignored()
    {
        var unlocalizedHost = await HostFixture.StartAsync(useMiddleware: false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en");

            using var response = await unlocalizedHost.Client.SendAsync(request);

            Assert.Empty(response.Content.Headers.ContentLanguage);
        }
        finally
        {
            await unlocalizedHost.DisposeAsync();
        }
    }

    /// <summary>
    /// 支持 zh-CN（默认）与 en 的最小宿主，回显当前区域性、界面区域性与一条框架通用词条。
    /// 宿主不含逐测可变状态，由同一测试类的用例共享。
    /// </summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        private IHost _host = default!;

        public HttpClient Client { get; private set; } = default!;

        public static async Task<HostFixture> StartAsync(bool useMiddleware)
        {
            var host = new HostFixture();
            await host.StartCoreAsync(useMiddleware);
            return host;
        }

        public Task InitializeAsync() => StartCoreAsync(useMiddleware: true);

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }

        private async Task StartCoreAsync(bool useMiddleware)
        {
            _host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services => services
                        .AddLogging()
                        .AddJsonLocalization(options => options.SupportedCultures = ["zh-CN", "en"]))
                    .Configure(app =>
                    {
                        if (useMiddleware)
                        {
                            app.UseJsonRequestLocalization();
                        }

                        app.Run(async context =>
                        {
                            var localizer = context.RequestServices.GetRequiredService<IStringLocalizer>();
                            await context.Response.WriteAsync(
                                $"{CultureInfo.CurrentCulture.Name}|{CultureInfo.CurrentUICulture.Name}|{localizer["Title:404"].Value}");
                        });
                    }))
                .StartAsync();

            Client = _host.GetTestClient();
        }
    }
}
