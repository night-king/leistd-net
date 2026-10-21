using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.Data.Paging;
using Leistd.ExceptionHandling;
using Leistd.Notifications.Errors;
using Leistd.Notifications.Stores;
using Leistd.Notifications.AspNetCore.Endpoints;
using Leistd.Notifications.Dtos;
using Leistd.Security.AspNetCore;
using Leistd.Security.Claims;
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

namespace Leistd.Notifications.Tests.AspNetCore;

/// <summary>通知中心端点：走宿主给的具名策略、只作用于当前用户、列表条数有上限。</summary>
/// <remarks>收件人若可由参数指定，任何登录用户都能读、删别人的通知；条数没有上限时一次请求能把整张表拉回来。</remarks>
public sealed class NotificationEndpointTests : IAsyncLifetime
{
    private const string AccessPolicy = "App.CurrentUser";
    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private readonly CapturingStore _store = new();
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
                    services.AddSingleton<INotificationStore>(_store);
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, SubjectHandler>("Test", _ => { });
                    services.AddAuthorizationBuilder()
                        .AddPolicy(AccessPolicy, policy => policy.RequireAuthenticatedUser());
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
                            context.Response.StatusCode = exception.Code == NotificationErrorCodes.IdentityCannotOperate
                                ? StatusCodes.Status403Forbidden
                                : StatusCodes.Status400BadRequest;
                            context.Response.Headers["X-Error-Code"] = exception.Code;
                        }
                    });
                    app.UseEndpoints(endpoints => endpoints.MapGroup("/api/v1/notifications")
                        .MapNotifications(options => options.AccessPolicy = AccessPolicy));
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

    /// <summary>漏配策略名在映射时就失败，而不是留一个"谁都能进"的通知中心。</summary>
    [Fact]
    public void Mapping_without_an_access_policy_fails()
    {
        var services = new ServiceCollection().AddRouting().AddSecurity().BuildServiceProvider();
        var endpoints = new FakeEndpointRouteBuilder(services);

        var error = Assert.Throws<ArgumentException>(() => endpoints.MapNotifications(_ => { }));

        Assert.Contains(nameof(NotificationEndpointOptions.AccessPolicy), error.Message);
    }

    private sealed class FakeEndpointRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? subject)
    {
        var request = new HttpRequestMessage(method, url);
        if (subject is not null)
        {
            request.Headers.Add("X-Subject", subject);
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/notifications", null)).StatusCode);
    }

    /// <summary>收件人取自当前主体，列表条数收敛到上限。</summary>
    [Fact]
    public async Task The_list_belongs_to_the_current_user_and_is_capped()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/notifications?maxCount=5000&unreadOnly=true", UserId.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((UserId.ToString(), NotificationEndpoints.MaximumListCount, true), _store.LastQuery);
        Assert.Equal("n1", (await response.Content.ReadFromJsonAsync<NotificationOutputDto[]>())![0].Title);
    }

    /// <summary>当前身份不是用户（机器客户端）：读取返回空，写入以带码的 403 拒绝。</summary>
    [Fact]
    public async Task A_non_user_identity_reads_nothing_and_cannot_modify()
    {
        var list = await SendAsync(HttpMethod.Get, "/api/v1/notifications", "client:reporting");
        var clear = await SendAsync(HttpMethod.Delete, "/api/v1/notifications", "client:reporting");

        Assert.Equal("[]", await list.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, clear.StatusCode);
        Assert.Equal(NotificationErrorCodes.IdentityCannotOperate, clear.Headers.GetValues("X-Error-Code").Single());
        Assert.Null(_store.LastDeletedUser);
    }

    [Theory]
    [InlineData("PUT", "/api/v1/notifications/n1/read")]
    [InlineData("PUT", "/api/v1/notifications/read-all")]
    [InlineData("DELETE", "/api/v1/notifications/n1")]
    [InlineData("DELETE", "/api/v1/notifications")]
    public async Task Modifications_act_on_the_current_user(string method, string url)
    {
        var response = await SendAsync(new HttpMethod(method), url, UserId.ToString());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(UserId.ToString(), _store.LastModifiedUser);
    }

    private sealed class CapturingStore : INotificationStore
    {
        public (string UserId, int Limit, bool UnreadOnly)? LastQuery { get; private set; }
        public string? LastModifiedUser { get; private set; }
        public string? LastDeletedUser { get; private set; }

        public Task SaveAsync(NotificationOutputDto notification, string userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<PagedResult<NotificationOutputDto>> GetByUserAsync(string userId, PageRequest page, bool unreadOnly = false, CancellationToken ct = default)
        {
            LastQuery = (userId, page.Limit, unreadOnly);
            return Task.FromResult(new PagedResult<NotificationOutputDto>(1,
                [new NotificationOutputDto { Id = "n1", Title = "n1", CreationTime = DateTime.UtcNow }]));
        }

        public Task MarkAsReadAsync(string notificationId, string userId, CancellationToken ct = default) => Record(userId);
        public Task MarkAllAsReadAsync(string userId, CancellationToken ct = default) => Record(userId);
        public Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default) => Task.FromResult(1);

        public async Task<bool> DeleteAsync(string notificationId, string userId, CancellationToken ct = default)
        {
            await Record(userId);
            return true;
        }

        public async Task<int> DeleteAllAsync(string userId, CancellationToken ct = default)
        {
            await Record(userId);
            LastDeletedUser = userId;
            return 1;
        }

        private Task Record(string userId)
        {
            LastModifiedUser = userId;
            return Task.CompletedTask;
        }
    }

    // X-Subject 头决定主体的 sub；没有该头即匿名
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

            var identity = new ClaimsIdentity([new Claim(CustomClaimTypes.Subject, subject.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
