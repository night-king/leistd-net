using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.ExceptionHandling;
using Leistd.Security.AspNetCore;
using Leistd.Security.Claims;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Leistd.Settings.Stores;
using Leistd.Settings.AspNetCore.Endpoints;
using Leistd.Settings.Dtos;
using Leistd.Settings.Tests.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Leistd.Settings.Tests.TestDoubles;

namespace Leistd.Settings.Tests.AspNetCore;

/// <summary>
/// 设置端点：两条策略名都由宿主给，自用端点走 <c>AccessPolicy</c>、改租户值走 <c>TenantWritePolicy</c>，
/// 登记了分组策略的分组再加一道；读取按同一判据给出 <c>CanManage</c>。取值错误是带码的 400。
/// </summary>
public sealed class SettingEndpointTests : IAsyncLifetime
{
    private const string AccessPolicy = "App.CurrentUser";
    private const string TenantWritePolicy = "App.Settings";
    private const string SecurityPolicy = "App.Settings.Security";
    private const string JwtGroupPolicy = "App.Settings.Integration";
    private const string ResourceGroupPolicy = "App.Settings.Resource";
    private const string FeatureGroupPolicy = "App.Settings.Feature";
    private static readonly Guid UserId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly FakeSettingStore _store = new();
    private IHost _host = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSecurity();
                    services.AddSettingsCore();
                    services.AddSingleton<ISettingStore>(_store);
                    services.AddSingleton<ISettingDefinitionProvider, Definitions>();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, SubjectHandler>("Test", _ => { })
                        .AddScheme<AuthenticationSchemeOptions, JwtLikeHandler>("Jwt", _ => { });
                    services.AddAuthorizationBuilder()
                        .AddPolicy(AccessPolicy, policy => policy.RequireAuthenticatedUser())
                        .AddPolicy(TenantWritePolicy, policy => policy.RequireClaim("permission", TenantWritePolicy))
                        .AddPolicy(SecurityPolicy, policy => policy.RequireClaim("permission", SecurityPolicy))
                        // 只认 Jwt 方案的身份：默认方案上的同名声明不算
                        .AddPolicy(JwtGroupPolicy, policy => policy
                            .AddAuthenticationSchemes("Jwt")
                            .RequireClaim("permission", JwtGroupPolicy))
                        // 依赖资源是当前请求，与端点授权一致
                        .AddPolicy(ResourceGroupPolicy, policy => policy.RequireAssertion(context => context.Resource is HttpContext))
                        // 依赖请求上原有的认证结果：别的分组按自带方案评估之后，它必须仍然在
                        .AddPolicy(FeatureGroupPolicy, policy => policy.RequireAssertion(context =>
                            context.Resource is HttpContext http
                            && http.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Succeeded == true));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    // 把业务异常换成状态码与错误码头，只为断言；宿主里由异常处理组件输出 Problem Details
                    app.Use(async (context, next) =>
                    {
                        try
                        {
                            await next(context);
                        }
                        catch (BusinessException exception)
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            context.Response.Headers["X-Error-Code"] = exception.Code;
                        }
                    });
                    app.UseEndpoints(endpoints => endpoints.MapGroup("/api/v1/settings")
                        .MapSettings(options =>
                        {
                            options.AccessPolicy = AccessPolicy;
                            options.TenantWritePolicy = TenantWritePolicy;
                            options.GroupPolicies["Security"] = SecurityPolicy;
                            options.GroupPolicies["Integration"] = JwtGroupPolicy;
                            options.GroupPolicies["Resource"] = ResourceGroupPolicy;
                            options.GroupPolicies["Feature"] = FeatureGroupPolicy;
                        }));
                }))
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    [InlineData(nameof(SettingEndpointOptions.AccessPolicy))]
    [InlineData(nameof(SettingEndpointOptions.TenantWritePolicy))]
    public void Mapping_without_a_policy_name_fails(string missing)
    {
        var endpoints = new FakeEndpointRouteBuilder();

        var error = Assert.Throws<ArgumentException>(() => endpoints.MapSettings(options =>
        {
            if (missing != nameof(SettingEndpointOptions.AccessPolicy))
            {
                options.AccessPolicy = AccessPolicy;
            }
        }));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "", null)).StatusCode);
    }

    [Fact]
    public async Task Any_user_reads_and_changes_their_own_preferences()
    {
        var list = await SendAsync(HttpMethod.Get, "", UserId.ToString());
        var write = await SendAsync(HttpMethod.Put, "/current-user", UserId.ToString(), new SetSettingInputDto("Display.TimeZone", "Asia/Tokyo"));

        Assert.Contains(await list.Content.ReadFromJsonAsync<SettingOutputDto[]>() ?? [], s => s.Name == "Display.TimeZone");
        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);
        Assert.Equal(("Display.TimeZone", SettingScopes.User, UserId.ToString()), (_store.Writes[0].Name, _store.Writes[0].Scope, _store.Writes[0].UserId));
    }

    [Fact]
    public async Task Tenant_writes_need_the_configured_policy()
    {
        var input = new SetSettingInputDto("Export.MaxRowsPerFile", "100");

        var denied = await SendAsync(HttpMethod.Put, "/current-tenant", UserId.ToString(), input);
        var granted = await SendAsync(HttpMethod.Put, "/current-tenant", UserId.ToString(), input, TenantWritePolicy);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        Assert.Equal(SettingScopes.Tenant, Assert.Single(_store.Writes).Scope);
    }

    [Theory]
    [InlineData(TenantWritePolicy)]
    [InlineData(SecurityPolicy)]
    public async Task A_group_with_its_own_policy_needs_both_policies_to_write(string onlyPolicy)
    {
        var input = new SetSettingInputDto("Security.RequireTwoFactor", "true");

        var denied = await SendAsync(HttpMethod.Put, "/current-tenant", UserId.ToString(), input, onlyPolicy);
        var granted = await SendAsync(
            HttpMethod.Put, "/current-tenant", UserId.ToString(), input, TenantWritePolicy, SecurityPolicy);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        Assert.Single(_store.Writes);
    }

    [Fact]
    public async Task Reading_hands_management_values_only_to_those_who_can_manage_them()
    {
        var member = await ReadAsync();
        var groupOnly = await ReadAsync(SecurityPolicy);
        var tenantOnly = await ReadAsync(TenantWritePolicy);
        var manager = await ReadAsync(TenantWritePolicy, SecurityPolicy);

        // 允许用户覆盖的照常下发，供个人设置页；只有能写租户层时才算可管理
        Assert.False(member["Display.TimeZone"].CanManage);
        Assert.True(tenantOnly["Display.TimeZone"].CanManage);
        // 只允许用户层的设置在系统视图里恒不可管理
        Assert.False(manager["Display.Density"].CanManage);

        // 不允许用户覆盖的设置：不能管理就不下发
        Assert.DoesNotContain("Security.RequireTwoFactor", member.Keys);
        Assert.DoesNotContain("Security.RequireTwoFactor", groupOnly.Keys);
        Assert.DoesNotContain("Security.RequireTwoFactor", tenantOnly.Keys);
        Assert.True(manager["Security.RequireTwoFactor"].CanManage);

        // 未登记分组策略的分组只看 TenantWritePolicy
        Assert.DoesNotContain("Export.MaxRowsPerFile", member.Keys);
        Assert.True(tenantOnly["Export.MaxRowsPerFile"].CanManage);
    }

    [Theory]
    [InlineData(" ", SecurityPolicy)]
    [InlineData("Security", " ")]
    public void Mapping_with_an_empty_group_or_policy_fails(string group, string policy)
    {
        var endpoints = new FakeEndpointRouteBuilder();

        Assert.Throws<ArgumentException>(() => endpoints.MapSettings(options =>
        {
            options.AccessPolicy = AccessPolicy;
            options.TenantWritePolicy = TenantWritePolicy;
            options.GroupPolicies[group] = policy;
        }));
    }

    // 分组策略指定了认证方案时照常生效：默认方案上的声明不能满足只认 Jwt 的策略
    [Fact]
    public async Task A_group_policy_authenticates_with_its_own_schemes()
    {
        var input = new SetSettingInputDto("Integration.Endpoint", "https://example.test");

        var defaultOnly = await SendAsync(
            HttpMethod.Put, "/current-tenant", UserId.ToString(), input, TenantWritePolicy, JwtGroupPolicy);
        var readDefaultOnly = await ReadAsync(TenantWritePolicy, JwtGroupPolicy);
        var withJwt = await SendAsync(
            HttpMethod.Put, "/current-tenant", UserId.ToString(), input, [TenantWritePolicy], jwtPermission: JwtGroupPolicy);

        Assert.Equal(HttpStatusCode.Unauthorized, defaultOnly.StatusCode);
        Assert.DoesNotContain("Integration.Endpoint", readDefaultOnly.Keys);
        Assert.Equal(HttpStatusCode.NoContent, withJwt.StatusCode);
        // 评估完方案后还原请求主体：之后的分组仍按默认方案的身份判定
        Assert.Single(_store.Writes);
    }

    [Fact]
    public async Task Group_policies_see_the_request_as_their_resource()
    {
        var read = await ReadAsync(TenantWritePolicy);
        var write = await SendAsync(
            HttpMethod.Put, "/current-tenant", UserId.ToString(), new SetSettingInputDto("Resource.Flag", "on"), TenantWritePolicy);

        Assert.True(read["Resource.Flag"].CanManage);
        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);
    }

    // 先按 Jwt 方案评估了 Integration 分组之后，请求原有的认证结果仍在：读写对同一设置的判定一致
    [Fact]
    public async Task Evaluating_a_policy_with_its_own_scheme_leaves_the_request_authentication_intact()
    {
        var read = await ReadAsync(TenantWritePolicy, JwtGroupPolicy);
        var write = await SendAsync(
            HttpMethod.Put, "/current-tenant", UserId.ToString(), new SetSettingInputDto("Feature.Flag", "on"), TenantWritePolicy);

        Assert.DoesNotContain("Integration.Endpoint", read.Keys);
        Assert.True(read["Feature.Flag"].CanManage);
        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);
    }

    // 不可见的设置照常是 404（这里由测试管道转成带码的 400），不因所在分组的策略提前变成 403
    [Fact]
    public async Task A_hidden_setting_in_a_restricted_group_is_still_not_available()
    {
        var response = await SendAsync(
            HttpMethod.Put, "/current-tenant", UserId.ToString(), new SetSettingInputDto("Security.Hidden", "x"), TenantWritePolicy);

        Assert.Equal(SettingErrorCodes.NotAvailable, response.Headers.GetValues("X-Error-Code").Single());
    }

    private async Task<Dictionary<string, SettingOutputDto>> ReadAsync(params string[] permissions)
    {
        var response = await SendAsync(HttpMethod.Get, "", UserId.ToString(), null, permissions);
        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<SettingOutputDto[]>() ?? [];
        return settings.ToDictionary(setting => setting.Name);
    }

    [Fact]
    public async Task An_invalid_value_is_a_coded_bad_request()
    {
        var response = await SendAsync(
            HttpMethod.Put, "/current-tenant", UserId.ToString(), new SetSettingInputDto("Security.RequireTwoFactor", "yes"), TenantWritePolicy, SecurityPolicy);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SettingErrorCodes.BooleanRequired, response.Headers.GetValues("X-Error-Code").Single());
        Assert.Empty(_store.Writes);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? subject,
        SetSettingInputDto? body = null,
        params string[] permissions)
        => SendAsync(method, path, subject, body, permissions, jwtPermission: null);

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? subject,
        SetSettingInputDto? body,
        string[] permissions,
        string? jwtPermission)
    {
        var request = new HttpRequestMessage(method, "/api/v1/settings" + path);
        if (subject is not null)
        {
            request.Headers.Add("X-Subject", subject);
        }

        if (permissions.Length > 0)
        {
            request.Headers.Add("X-Permission", string.Join(',', permissions));
        }

        if (jwtPermission is not null)
        {
            request.Headers.Add("X-Jwt-Permission", jwtPermission);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private sealed class Definitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Display.TimeZone", scopes: SettingScopes.All).IsVisibleToClients = true;
            context.Add("Security.RequireTwoFactor", "false", group: "Security").AsBoolean().IsVisibleToClients = true;
            context.Add("Export.MaxRowsPerFile", "500").AsInteger(1, 10000).IsVisibleToClients = true;
            context.Add("Display.Density", "comfortable", SettingScopes.User).IsVisibleToClients = true;
            context.Add("Integration.Endpoint", group: "Integration").IsVisibleToClients = true;
            context.Add("Resource.Flag", group: "Resource").IsVisibleToClients = true;
            context.Add("Feature.Flag", group: "Feature").IsVisibleToClients = true;
            context.Add("Security.Hidden", group: "Security");
        }
    }

    private sealed class FakeEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    // X-Subject 决定主体的 sub，X-Permission 给一个权限声明；没有 X-Subject 即匿名
    private sealed class SubjectHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Subject", out var subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims = [new(CustomClaimTypes.Subject, subject.ToString())];
            if (Request.Headers.TryGetValue("X-Permission", out var permission))
            {
                claims.AddRange(permission.ToString().Split(',').Select(value => new Claim("permission", value)));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    // 只认 X-Jwt-Permission：模拟另一种认证方案，与默认方案的身份互不相干
    private sealed class JwtLikeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Jwt-Permission", out var permission))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(CustomClaimTypes.Subject, UserId.ToString()), new Claim("permission", permission.ToString())],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
