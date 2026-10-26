#if (LocalIdentity)
using System.Net;
#if (OpenIddictServer)
using System.Net.Http.Headers;
#endif
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.Settings.Constants;
#endif
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Settings.Errors;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>账户偏好由本服务持有：<c>/me</c> 带生效值与个人覆盖，<c>PATCH /me/preferences</c> 一次改一项并返回提交后的完整表示。</summary>
/// <remarks>每个用例新建自己的用户，个人层互不影响；改组织值的用例在结束时清掉。</remarks>
public sealed class AccountPreferencesTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    private const string PatchPath = "/api/v1/auth/me/preferences";

    [Fact]
    public async Task Me_carries_the_effective_preferences_and_the_personal_overrides()
    {
        using var session = await NewUserSessionAsync();

        var me = await GetMeAsync(session.Client);

        // 外观三层都没设时取定义默认值「跟随系统」；本人没有覆盖
        Assert.Equal("system", Field(me, "preferences", "theme"));
        Assert.Null(Field(me, "preferenceOverrides", "theme"));
    }

    [Fact]
    public async Task Patching_one_field_returns_the_committed_representation_and_me_follows()
    {
        using var session = await NewUserSessionAsync();

        using var response = await PatchAsync(session.Client, """{"theme":"dark"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        var written = await ReadAsync(response);
        Assert.Equal("dark", Field(written, "preferences", "theme"));
        Assert.Equal("dark", Field(written, "preferenceOverrides", "theme"));
        Assert.Equal("dark", (await GetMeAsync(session.Client)).GetProperty("preferences").GetProperty("theme").GetString());
    }

    // 清除个人覆盖后生效值回落到组织值：只有持有方算得出来，响应直接给出，前端不必推导
    [Fact]
    public async Task Clearing_an_override_returns_the_organization_value_as_effective()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        await SetOrganizationTimeZoneAsync(admin.Client, "Asia/Tokyo");
        try
        {
            using var session = await NewUserSessionAsync(admin.Client);
            (await PatchAsync(session.Client, """{"timeZone":"Europe/Paris"}""")).Dispose();

            using var cleared = await PatchAsync(session.Client, """{"timeZone":null}""");

            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            var representation = await ReadAsync(cleared);
            Assert.Equal("Asia/Tokyo", Field(representation, "preferences", "timeZone"));
            Assert.Null(Field(representation, "preferenceOverrides", "timeZone"));
        }
        finally
        {
            await SetOrganizationTimeZoneAsync(admin.Client, null);
        }
    }

    // 一次只接受一个已知字段，值为字符串或 null：请求体的形状是字段校验错误，不是业务错误码
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"theme":"dark","timeZone":"UTC"}""")]
    [InlineData("""{"color":"red"}""")]
    [InlineData("""{"theme":1}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task A_patch_that_is_not_exactly_one_known_field_is_rejected(string body)
    {
        using var session = await NewUserSessionAsync();

        using var response = await PatchAsync(session.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("urn:leistd:problem:validation-error", problem.RootElement.GetProperty("type").GetString());
        Assert.False(problem.RootElement.TryGetProperty("code", out _));
    }

    [Theory]
    [InlineData("""{"theme":"sepia"}""", SettingErrorCodes.ValueNotAllowed)]
    [InlineData("""{"timeZone":"Mars/Olympus"}""", AppSettingErrorCodes.TimeZoneInvalid)]
    public async Task A_value_outside_the_setting_domain_is_rejected_and_nothing_changes(string body, string code)
    {
        using var session = await NewUserSessionAsync();

        using var response = await PatchAsync(session.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await ErrorCodeAsync(response));
        var me = await GetMeAsync(session.Client);
        Assert.Null(Field(me, "preferenceOverrides", "theme"));
        Assert.Null(Field(me, "preferenceOverrides", "timeZone"));
    }

    [Fact]
    public async Task Anonymous_callers_cannot_write_preferences()
    {
        using var anonymous = Factory.CreateProjectClient();

        using var response = await PatchAsync(anonymous, """{"theme":"dark"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
#if (OpenIddictServer)

    /// <summary>资源服务按用户读取：只认带 <c>account-preferences.read</c> 的机器令牌，带强 ETag，未变时 304。</summary>
    [Fact]
    public async Task The_machine_endpoint_serves_preferences_with_an_etag_to_the_scoped_machine_only()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await CreateUserAsync(admin.Client);
        var path = $"/api/v1/internal/account-preferences/host/{user.Id}";
        using var machine = await CreateMachineClientAsync(Factory, admin.Client, AccountPreferenceScopes.Read);

        using var first = await machine.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        Assert.True(etag.IsWeak is false);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(etag);
        using var notModified = await machine.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        // 本人改了以后表示变了，ETag 跟着变
        using var session = await Factory.LoginAsync(user.Username, TestPassword);
        (await PatchAsync(session.Client, """{"theme":"light"}""")).Dispose();
        using var changed = new HttpRequestMessage(HttpMethod.Get, path);
        changed.Headers.IfNoneMatch.Add(etag);
        using var fresh = await machine.SendAsync(changed);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.NotEqual(etag, fresh.Headers.ETag);
        Assert.Equal("light", (await ReadAsync(fresh)).GetProperty("preferenceOverrides").GetProperty("theme").GetString());
    }

    // If-None-Match 的合法形式（RFC 9110 §13.1.2）：标签列表、弱标签按弱比较命中，* 匹配任何现有表示
    [Theory]
    [InlineData("list")]
    [InlineData("weak")]
    [InlineData("any")]
    public async Task The_machine_endpoint_honours_every_valid_if_none_match_form(string form)
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await CreateUserAsync(admin.Client);
        var path = $"/api/v1/internal/account-preferences/host/{user.Id}";
        using var machine = await CreateMachineClientAsync(Factory, admin.Client, AccountPreferenceScopes.Read);
        using var first = await machine.GetAsync(path);
        var etag = first.Headers.ETag!.Tag;

        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", form switch
        {
            "list" => $"\"something-else\", {etag}",
            "weak" => $"W/{etag}",
            _ => "*",
        });
        using var response = await machine.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task The_machine_endpoint_does_not_fall_back_to_the_host_for_an_unknown_tenant_or_user()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await CreateUserAsync(admin.Client);
        using var machine = await CreateMachineClientAsync(Factory, admin.Client, AccountPreferenceScopes.Read);

        Assert.Equal(HttpStatusCode.NotFound, (await machine.GetAsync($"/api/v1/internal/account-preferences/not-a-tenant/{user.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await machine.GetAsync($"/api/v1/internal/account-preferences/{Guid.CreateVersion7()}/{user.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await machine.GetAsync($"/api/v1/internal/account-preferences/host/{Guid.CreateVersion7()}")).StatusCode);
    }

    [Fact]
    public async Task People_and_machines_without_the_scope_are_refused_by_the_machine_endpoint()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await CreateUserAsync(admin.Client);
        var path = $"/api/v1/internal/account-preferences/host/{user.Id}";
        using var anonymous = Factory.CreateProjectClient();
        using var otherMachine = await CreateMachineClientAsync(Factory, admin.Client, new Application.Auth.Options.OAuthResourceOptions().Resource);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        // 管理员的浏览器会话不是机器主体
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherMachine.GetAsync(path)).StatusCode);
    }

    /// <summary>授予 profile 时 userinfo 按 OIDC 标准给出生效的 <c>locale</c> 与 <c>zoneinfo</c>，供第三方依赖方使用。</summary>
    [Fact]
    public async Task Userinfo_carries_locale_and_zoneinfo_under_the_profile_scope()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await CreateUserAsync(admin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);
        (await PatchAsync(session.Client, """{"timeZone":"Asia/Tokyo"}""")).Dispose();
#if (IncludeLocalization)
        (await PatchAsync(session.Client, """{"language":"zh-CN"}""")).Dispose();
#endif
        var (clientId, clientSecret) = await CreateClientAsync(admin.Client, "profile");
        var tokens = await AuthorizeAndExchangeAsync(Factory, session, clientId, clientSecret, "profile");

        using var api = CreateHttpsClient(Factory);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var userinfo = await api.GetAsync("/connect/userinfo");

        Assert.Equal(HttpStatusCode.OK, userinfo.StatusCode);
        var claims = await ReadAsync(userinfo);
        Assert.Equal("Asia/Tokyo", claims.GetProperty("zoneinfo").GetString());
#if (IncludeLocalization)
        Assert.Equal("zh-CN", claims.GetProperty("locale").GetString());
#endif
    }
#endif

    private async Task<AuthenticatedSession> NewUserSessionAsync(HttpClient? admin = null)
    {
        using var ownAdmin = admin is null ? await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword) : null;
        var user = await CreateUserAsync(admin ?? ownAdmin!.Client);
        return await Factory.LoginAsync(user.Username, TestPassword);
    }

    private static async Task SetOrganizationTimeZoneAsync(HttpClient admin, string? value)
    {
        using var response = await admin.PutAsJsonAsync(
            "/api/v1/settings/current-tenant",
            new { Name = SettingConstant.Display.TimeZone, Value = value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/merge-patch+json");
        return await client.PatchAsync(PatchPath, content);
    }

    // 响应忽略 null 值（API 规范）：对象内未设置的字段不出现，按 null 读
    private static string? Field(JsonElement representation, string part, string name)
        => representation.TryGetProperty(part, out var values) && values.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;

    private static async Task<JsonElement> GetMeAsync(HttpClient client)
        => await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
#endif
