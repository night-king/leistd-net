using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.AspNetCore;
using Leistd.OperationRecords.AspNetCore.Attributes;
using Leistd.OperationRecords.AspNetCore.Extensions;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.Security.Users;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Leistd.Tracing.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.OperationRecords.Tests.AspNetCore;

/// <summary>兜底记录的目标：有类型依据的 Guid 路由段归一为小写 D，字符串标识保留原文。</summary>
/// <remarks>
/// <para>应用服务用 <c>OperationTarget.For(Guid)</c> 记小写 D，去重按目标逐字比较。路由原文是大写或 N 格式时，
/// 一次失败会记两条，按目标也检索不到兜底那条。</para>
/// <para>反过来，字符串主键可以恰好是 32 位十六进制或 Guid 外观；没有类型依据就改写它，同样制造重复与检索不一致。
/// 因此走真实路由与 MVC / Minimal API 元数据，覆盖授权被拒与业务失败两条兜底路径。</para>
/// </remarks>
public sealed class RouteTargetNormalizationTests(RouteTargetNormalizationTests.Host host)
    : IClassFixture<RouteTargetNormalizationTests.Host>
{
    private const string Upper = "0123ABCD-89AB-CDEF-0123-456789ABCDEF";
    private const string NFormat = "0123abcd89abcdef0123456789abcdef";
    private const string Lower = "0123abcd-89ab-cdef-0123-456789abcdef";
    private const string HexKey = "0123456789abcdef0123456789abcdef";

    /// <summary>业务失败：应用服务已按自己的类型记过，兜底须与之逐字一致而跳过。</summary>
    [Theory]
    [InlineData("/mvc/users/" + Upper, Lower)]
    [InlineData("/mvc/users/" + NFormat, Lower)]
    [InlineData("/mvc/aliased/" + Upper, Lower)]
    [InlineData("/minimal/users/" + NFormat, Lower)]
    [InlineData("/minimal/aliased/" + Upper, Lower)]
    [InlineData("/minimal/constrained/" + Upper, Lower)]
    [InlineData("/mvc/apps/" + HexKey, HexKey)]
    [InlineData("/mvc/apps/" + Upper, Upper)]
    [InlineData("/mvc/shadowed/" + HexKey + "?id=" + Lower, HexKey)]
    [InlineData("/minimal/shadowed/" + HexKey + "?id=" + Lower, HexKey)]
    [InlineData("/mvc/tenants/" + Upper + "/settings/" + HexKey, "Setting/" + Lower + "/" + HexKey)]
    public async Task A_business_failure_already_recorded_by_the_application_is_recorded_once(string path, string expectedTarget)
    {
        var written = await SendAsync(path, permission: "write");

        Assert.Equal(expectedTarget, Assert.Single(written).TargetId);
    }

    /// <summary>授权被拒：只有兜底一条，目标写法与成功路径一致才能按目标查到。</summary>
    [Theory]
    [InlineData("/mvc/users/" + Upper, Lower)]
    [InlineData("/mvc/aliased/" + NFormat, Lower)]
    [InlineData("/minimal/users/" + Upper, Lower)]
    [InlineData("/minimal/aliased/" + NFormat, Lower)]
    [InlineData("/minimal/constrained/" + NFormat, Lower)]
    [InlineData("/mvc/apps/" + HexKey, HexKey)]
    [InlineData("/mvc/apps/" + Upper, Upper)]
    [InlineData("/mvc/shadowed/" + HexKey + "?id=" + Lower, HexKey)]
    [InlineData("/minimal/shadowed/" + HexKey + "?id=" + Lower, HexKey)]
    [InlineData("/mvc/tenants/" + NFormat + "/settings/" + Upper, "Setting/" + Lower + "/" + Upper)]
    public async Task A_denied_request_records_the_target_in_the_application_format(string path, string expectedTarget)
    {
        var written = await SendAsync(path, permission: "none");

        var record = Assert.Single(written);
        Assert.Equal("Error:Forbidden", record.FailureCode);
        Assert.Equal(expectedTarget, record.TargetId);
    }

    private async Task<List<OperationRecordInfo>> SendAsync(string path, string permission)
    {
        host.Store.Written.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        request.Headers.Add("X-Permission", permission);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(permission == "write" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, response.StatusCode);
        return [.. host.Store.Written];
    }

    /// <summary>挂真实路由、MVC 与 Minimal API 的宿主；业务失败由外层转成 409，被拒走宿主的授权结果处理器。</summary>
    public sealed class Host : IAsyncLifetime
    {
        private IHost _host = default!;

        internal RecordingOperationRecordStore Store { get; } = new();

        internal HttpClient Client { get; private set; } = default!;

        public async Task InitializeAsync()
        {
            _host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddLogging();
                        services.AddSingleton<IOperationRecordWriter>(Store);
                        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant(null));
                        services.AddSingleton<ICurrentUser>(new FakeCurrentUser());
                        services.AddSingleton<ICorrelationIdProvider>(new FakeCorrelationIdProvider(null));
                        services.AddSingleton<IClock>(new UtcClockProvider(new FakeTimeProvider()));
                        services.AddSingleton<IOperationActionDefinitionManager>(new FakeOperationActionDefinitionManager());
                        services.AddOperationRecords();
                        services.AddRouting();
                        services.AddControllers().AddApplicationPart(typeof(GuidTargetController).Assembly);
                        services.AddAuthentication("Test")
                            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("Test", _ => { });
                        services.AddAuthorization(options =>
                            options.AddPolicy("write", policy => policy.RequireClaim("permission", "write")));
                        services.AddSingleton<IAuthorizationMiddlewareResultHandler, RecordingResultHandler>();
                    })
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            try
                            {
                                await next(context);
                            }
                            catch (BusinessException)
                            {
                                context.Response.StatusCode = StatusCodes.Status409Conflict;
                            }
                        });
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseOperationFailureRecording();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();

                            // 类型依据来自处理方法的参数
                            endpoints.MapPut("/minimal/users/{id}", async (Guid id, IOperationRecorder recorder) =>
                                {
                                    await RecordAndRejectAsync(recorder, "users.updated", OperationTarget.For(id));
                                })
                                .RequireAuthorization("write")
                                .WithMetadata(new OperationRecordActionAttribute("users.updated", "id"));

                            endpoints.MapPut("/minimal/aliased/{userKey}",
                                    async ([FromRoute(Name = "userKey")] Guid id, IOperationRecorder recorder) =>
                                    {
                                        await RecordAndRejectAsync(recorder, "users.renamed", OperationTarget.For(id));
                                    })
                                .RequireAuthorization("write")
                                .WithMetadata(new OperationRecordActionAttribute("users.renamed", "userKey"));

                            // 参数是字符串，类型依据只来自 guid 约束
                            endpoints.MapPut("/minimal/constrained/{id:guid}", async (string id, IOperationRecorder recorder) =>
                                {
                                    await RecordAndRejectAsync(recorder, "users.locked", OperationTarget.For(Guid.Parse(id)));
                                })
                                .RequireAuthorization("write")
                                .WithMetadata(new OperationRecordActionAttribute("users.locked", "id"));

                            // 路由键绑到字符串，同名 Guid 显式取自查询：不是路由的类型依据
                            endpoints.MapPut("/minimal/shadowed/{id}",
                                    async ([FromRoute(Name = "id")] string routeId, [FromQuery] Guid id, IOperationRecorder recorder) =>
                                    {
                                        await RecordAndRejectAsync(recorder, "apps.updated", OperationTarget.For(routeId));
                                    })
                                .RequireAuthorization("write")
                                .WithMetadata(new OperationRecordActionAttribute("apps.updated", "id"));
                        });
                    }))
                .StartAsync();
            Client = _host.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    /// <summary>模拟应用服务：按自己的类型记一条失败，再抛业务异常让兜底看到。</summary>
    internal static async Task RecordAndRejectAsync(IOperationRecorder recorder, string action, OperationTarget target)
    {
        await recorder.RecordFailedAsync(action, target, "write", OperationFailure.FromCode("Test:Conflict"));
        throw new BusinessException("Test:Conflict", "Business rejection");
    }

    private sealed class RecordingResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public async Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Forbidden)
            {
                await context.RecordDeniedOperationAsync();
            }

            await _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Permission", out var permission))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("permission", permission.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

/// <summary>控制器形态的端点：类型依据来自动作参数元数据。</summary>
[ApiController]
[Route("mvc")]
[Authorize(Policy = "write")]
public sealed class GuidTargetController(IOperationRecorder recorder) : ControllerBase
{
    /// <summary>Guid 参数，路由无约束（与模板的用户、角色端点同形）。</summary>
    [HttpPut("users/{id}")]
    [OperationRecordAction("users.updated", "id")]
    public Task Update(Guid id)
        => RouteTargetNormalizationTests.RecordAndRejectAsync(recorder, "users.updated", OperationTarget.For(id));

    /// <summary>路由键经 <c>[FromRoute(Name)]</c> 别名绑定到 Guid 参数。</summary>
    [HttpPut("aliased/{userKey}")]
    [OperationRecordAction("users.renamed", "userKey")]
    public Task Rename([FromRoute(Name = "userKey")] Guid id)
        => RouteTargetNormalizationTests.RecordAndRejectAsync(recorder, "users.renamed", OperationTarget.For(id));

    /// <summary>字符串主键（与模板的开放应用端点同形），取值像 Guid 也不改写。</summary>
    [HttpPut("apps/{id}")]
    [OperationRecordAction("apps.updated", "id")]
    public Task UpdateApp(string id)
        => RouteTargetNormalizationTests.RecordAndRejectAsync(recorder, "apps.updated", OperationTarget.For(id));

    /// <summary>路由键绑到字符串，同名 Guid 显式取自查询：不是路由的类型依据，路由原文不改写。</summary>
    [HttpPut("shadowed/{id}")]
    [OperationRecordAction("apps.updated", "id")]
    public Task UpdateShadowed([FromRoute(Name = "id")] string routeId, [FromQuery] Guid id)
        => RouteTargetNormalizationTests.RecordAndRejectAsync(recorder, "apps.updated", OperationTarget.For(routeId));

    /// <summary>复合目标加前缀：Guid 段归一，字符串段原样。</summary>
    [HttpPut("tenants/{tenantId}/settings/{name}")]
    [OperationRecordAction("settings.updated", "tenantId", "name", TargetIdPrefix = "Setting/")]
    public Task UpdateSetting(Guid tenantId, string name)
        => RouteTargetNormalizationTests.RecordAndRejectAsync(
            recorder, "settings.updated", OperationTarget.For($"Setting/{tenantId}/{name}"));
}
