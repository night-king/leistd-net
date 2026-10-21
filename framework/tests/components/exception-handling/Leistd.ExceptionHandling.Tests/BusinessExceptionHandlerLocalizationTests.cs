using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ExceptionHandling.Descriptors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

public class BusinessExceptionHandlerLocalizationTests
{
    [Fact]
    public async Task Uses_code_as_resource_key_and_fills_named_placeholders()
    {
        using var host = await StartAsync(new StubLocalizer(new Dictionary<string, string>
        {
            ["User:EmailAlreadyUsed"] = "邮箱 '{Email}' 已被占用"
        }));

        var response = await host.GetTestClient().GetAsync("/");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("邮箱 'a@b.com' 已被占用", problem.GetProperty("detail").GetString());
        Assert.Equal("User:EmailAlreadyUsed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Falls_back_to_the_safe_business_message_without_a_resource()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Email already in use.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Type_mappings_use_their_code_for_localization_without_exposing_exception_details()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IStringLocalizer>(new StubLocalizer(new Dictionary<string, string>
                    {
                        ["Upstream:ContractBroken"] = "上游服务响应无效。"
                    }));
                    services.AddGlobalExceptionHandler(options =>
                        options.MapException<InvalidOperationException>(_ => new ExceptionDescriptor(
                            StatusCodes.Status502BadGateway, "Upstream:ContractBroken", "Upstream error.")));
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new InvalidOperationException("private endpoint"));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(502, (int)response.StatusCode);
        Assert.Equal("上游服务响应无效。", body.GetProperty("detail").GetString());
        Assert.DoesNotContain("private endpoint", body.ToString());
    }

    private static async Task<IHost> StartAsync(IStringLocalizer? localizer = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    if (localizer is not null)
                        services.AddSingleton(localizer);
                    services.AddGlobalExceptionHandler(_ => { });
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new BusinessException("User:EmailAlreadyUsed", "Email already in use.")
                        .WithData("Email", "a@b.com"));
                }))
            .StartAsync();
        return host;
    }

    private sealed class StubLocalizer(IReadOnlyDictionary<string, string> map) : IStringLocalizer
    {
        public LocalizedString this[string name] => map.TryGetValue(name, out var value)
            ? new LocalizedString(name, value, false)
            : new LocalizedString(name, name, true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            map.Select(pair => new LocalizedString(pair.Key, pair.Value, false));
    }
}
