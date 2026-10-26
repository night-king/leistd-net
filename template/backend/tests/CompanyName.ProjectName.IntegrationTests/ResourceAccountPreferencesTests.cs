#if (RemoteTokenAuth)
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Domain.Users.Abstractions;
using CompanyName.ProjectName.Infrastructure.AccountPreferences;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>资源服务不持有账户偏好：<c>/me</c> 的偏好取自 Identity，<c>PATCH /me/preferences</c> 转交 Identity，本地不定义 <c>Display.*</c>。</summary>
/// <remarks>Identity 在测试宿主里由内存替身代替；回源、缓存与转交的协议行为由单元测试对假接口钉住。</remarks>
public sealed class ResourceAccountPreferencesTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Me_carries_the_preferences_held_by_Identity()
    {
        var subject = Guid.CreateVersion7();
        factory.Services.GetRequiredService<InMemoryAccountPreferences>().Values[subject] =
            new() { TimeZone = "Asia/Tokyo", Theme = "dark" };
        using var session = factory.CreateResourceSession(subject, ProjectWebApplicationFactory.NewTenantId());

        var me = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");

        Assert.Equal("Asia/Tokyo", me.GetProperty("preferences").GetProperty("timeZone").GetString());
        Assert.Equal("dark", me.GetProperty("preferenceOverrides").GetProperty("theme").GetString());
    }

    [Fact]
    public async Task A_patch_is_handed_to_Identity_and_its_representation_returned()
    {
        var subject = Guid.CreateVersion7();
        using var session = factory.CreateResourceSession(subject, ProjectWebApplicationFactory.NewTenantId());

        using var response = await PatchAsync(session.Client, """{"theme":"light"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("light", body.GetProperty("preferences").GetProperty("theme").GetString());
        Assert.Equal("light", factory.Services.GetRequiredService<InMemoryAccountPreferences>().Values[subject].Theme);
    }

    // 转交前按同一套规则在本地判定：拒绝时给出与 Identity 相同的错误码与占位数据
    [Theory]
    [InlineData("""{"theme":"sepia"}""", SettingErrorCodes.ValueNotAllowed)]
    [InlineData("""{"timeZone":"Mars/Olympus"}""", AppSettingErrorCodes.TimeZoneInvalid)]
    public async Task An_invalid_patch_is_rejected_before_it_reaches_Identity(string body, string code)
    {
        using var session = factory.CreateResourceSession(Guid.CreateVersion7(), ProjectWebApplicationFactory.NewTenantId());

        using var response = await PatchAsync(session.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    // Identity 带业务码的拒绝原样交还：同码同状态，说明取 Identity 的 detail（本服务没有该码的词条），不带上游地址
    [Fact]
    public async Task A_business_refusal_from_Identity_keeps_its_status_code_and_public_detail()
    {
        await using var host = WithIdentityResponding(HttpStatusCode.Forbidden,
            """{"status":403,"code":"Review:PolicyRefusal","detail":"Localized policy refusal","traceId":"remote-trace"}""");
        using var session = ProjectWebApplicationFactory.CreateResourceSession(host, Guid.CreateVersion7(), ProjectWebApplicationFactory.NewTenantId());

        using var response = await PatchAsync(session.Client, """{"theme":"dark"}""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("Review:PolicyRefusal", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Localized policy refusal", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("issuer.internal.example", body);
        Assert.DoesNotContain("remote-trace", body);
    }

    // 不带业务码的 403 分不清用户凭证失效与交换配置错误：按依赖故障回 502，不让前端当成拒绝或登出
    [Fact]
    public async Task A_refusal_without_a_business_code_is_a_dependency_failure()
    {
        await using var host = WithIdentityResponding(HttpStatusCode.Forbidden, """{"status":403,"title":"Forbidden"}""");
        using var session = ProjectWebApplicationFactory.CreateResourceSession(host, Guid.CreateVersion7(), ProjectWebApplicationFactory.NewTenantId());

        using var response = await PatchAsync(session.Client, """{"theme":"dark"}""");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    // 偏好的设置定义只在持有方：资源服务的设置页不出这几项，写入也无从落到本地
    [Fact]
    public async Task The_resource_service_defines_no_display_settings_of_its_own()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ISettingDefinitionManager>();

        Assert.Null(definitions.GetOrNull(SettingConstant.Display.TimeZone));
        Assert.Null(definitions.GetOrNull(SettingConstant.Display.Theme));
    }

    // 换回真实的端口实现，只把 Identity 的写入接口换成固定应答：转交与错误转译走生产代码
    private WebApplicationFactory<Program> WithIdentityResponding(HttpStatusCode status, string problem)
        => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIdentityAccountPreferences>();
            services.AddTransient<IIdentityAccountPreferences, IdentityAccountPreferences>();
            services.RemoveAll<IIdentityAccountPreferencesWriteApi>();
            services.AddSingleton<IIdentityAccountPreferencesWriteApi>(new FixedWriteApi(status, problem));
        }));

    private sealed class FixedWriteApi(HttpStatusCode status, string problem) : IIdentityAccountPreferencesWriteApi
    {
        public Task<HttpResponseMessage> PatchAsync(JsonObject body, string? acceptLanguage, CancellationToken cancellationToken = default)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Patch, "https://issuer.internal.example/api/v1/auth/me/preferences"),
                Content = new StringContent(problem, Encoding.UTF8, "application/problem+json"),
            });
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/merge-patch+json");
        return await client.PatchAsync("/api/v1/auth/me/preferences", content);
    }
}
#endif
