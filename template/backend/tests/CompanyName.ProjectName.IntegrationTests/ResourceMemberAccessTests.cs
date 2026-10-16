#if (RemoteTokenAuth)
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Grants;
using Leistd.MultiTenancy.Context;
#if (IncludeOperationRecords)
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.Models;
#endif
using Leistd.Security.Claims;
#if (IncludeRealTime)
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>资源服务形态：本服务停用成员（或没有成员行）后，需要身份的端点在下一个请求就拒绝。</summary>
/// <remarks>
/// 启停归本服务，签发方令牌不知道它：此前只有 RBAC 路径看得见，<c>/me</c>、读设置这类只要求"当前用户"的端点照常 200。
/// </remarks>
public sealed class ResourceMemberAccessTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Issuer = "https://identity.test/";
    private const string Audience = "resource-api";

    [Fact]
    public async Task A_disabled_member_is_rejected_with_a_stable_code_and_regains_access_when_enabled()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        await GrantAsync(factory.Services, tenantId, subjectId, PermissionConstant.Users.Default);
        using var session = factory.CreateResourceSession(subjectId, tenantId);

        // 首次请求即投影，投影读到的状态直接用于授权
        using (var first = await session.Client.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var settings = await session.Client.GetAsync("/api/v1/settings"))
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        using (var users = await session.Client.GetAsync("/api/v1/users"))
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);

        await SetActiveAsync(factory.Services, tenantId, subjectId, active: false);

        await AssertRejectedAsync(session.Client, "/api/v1/auth/me", UserErrorCodes.LocalAccessDisabled);
        // 只要求"当前用户"的组件端点
        await AssertRejectedAsync(session.Client, "/api/v1/settings", UserErrorCodes.LocalAccessDisabled);
        // 带权限点的端点照旧拒绝
        using (var users = await session.Client.GetAsync("/api/v1/users"))
            Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);

        // 不跨请求缓存：重新启用后下一个请求就恢复
        await SetActiveAsync(factory.Services, tenantId, subjectId, active: true);
        using (var restored = await session.Client.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    /// <summary>成员不能停用自己：拒绝在写入之前，本人照常可用，只留被拒记录、不留成功记录。</summary>
    [Fact]
    public async Task A_member_cannot_disable_their_own_access()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        await GrantAsync(factory.Services, tenantId, subjectId, PermissionConstant.Users.Update);
        using var session = factory.CreateResourceSession(subjectId, tenantId);
        using (var first = await session.Client.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using (var disable = await session.Client.PatchAsync($"/api/v1/users/{subjectId}/disable", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);
            var problem = await disable.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(UserErrorCodes.SelfDisableForbidden, problem.GetProperty("code").GetString());
        }

        using (var me = await session.Client.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.True(await db.Set<User>().IgnoreQueryFilters().Where(user => user.Id == subjectId).Select(user => user.IsActive).SingleAsync());
#if (IncludeOperationRecords)
        Assert.False(await db.Set<OperationRecord>().IgnoreQueryFilters()
            .AnyAsync(record => record.Action == OperationRecordActions.UserDisabled && record.TargetId == subjectId.ToString()
                && record.Outcome == OperationRecordOutcome.Succeeded));
#else
        Assert.DoesNotContain(factory.Services.GetRequiredService<OperationRecordLogCapture>().Snapshot(), entry =>
            Equals(OperationRecordLogCapture.Field(entry, "OperationAction"), OperationRecordActions.UserDisabled)
            && Equals(OperationRecordLogCapture.Field(entry, "OperationTargetId"), subjectId.ToString())
            && OperationRecordLogCapture.Field(entry, "OperationOutcome")?.ToString() == "Succeeded");
#endif
    }

    [Fact]
    public async Task A_subject_without_a_local_row_is_rejected_as_missing_not_as_disabled()
    {
        var subjectId = Guid.CreateVersion7();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(new FailingUserInsert(subjectId)))));
        using var session = ProjectWebApplicationFactory.CreateResourceSession(host, subjectId, ProjectWebApplicationFactory.NewTenantId());

        // 投影失败不拦匿名端点
        using (var live = await session.Client.GetAsync("/api/health/live"))
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        await AssertRejectedAsync(session.Client, "/api/v1/auth/me", UserErrorCodes.LocalMemberMissing);
    }

    [Fact]
    public async Task A_failing_status_read_is_a_server_error_not_a_rejection()
    {
        var subjectId = Guid.CreateVersion7();
        var failing = new SubjectQueryCounter(subjectId) { Fail = true };
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(failing))));
        using var session = ProjectWebApplicationFactory.CreateResourceSession(host, subjectId, ProjectWebApplicationFactory.NewTenantId());

        using var response = await PutOwnTimeZoneAsync(session.Client);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // 投影读两次（首次加一次重试）都失败，授权阶段又按主键读一次
        Assert.Equal(3, failing.Count);
    }

    [Fact]
    public async Task A_normal_request_reuses_the_projection_read_instead_of_querying_the_status_again()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        var counter = new SubjectQueryCounter(subjectId);
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(counter))));
        using var session = ProjectWebApplicationFactory.CreateResourceSession(host, subjectId, tenantId);
        using (var warmUp = await PutOwnTimeZoneAsync(session.Client))
            Assert.Equal(HttpStatusCode.NoContent, warmUp.StatusCode);

        counter.Reset();
        using (var allowed = await PutOwnTimeZoneAsync(session.Client))
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        // 只有投影那一次主键读：授权阶段用的是它的快照
        Assert.Equal(1, counter.Count);

        await SetActiveAsync(host.Services, tenantId, subjectId, active: false);
        counter.Reset();
        await AssertRejectedAsync(session.Client, "/api/v1/settings", UserErrorCodes.LocalAccessDisabled);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task Production_policies_reject_a_disabled_member_and_leave_machine_clients_as_before()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "member-access-test" };
        using var production = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        await using var host = production.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", Audience)
            .ConfigureTestServices(services => services.Configure<OpenIddictValidationOptions>(options =>
            {
                options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer) };
                options.Configuration.SigningKeys.Add(key);
            })));
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        using var member = Bearer(host, Token(key, subjectId.ToString(), tenantId));

        using (var allowed = await member.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await SetActiveAsync(host.Services, tenantId, subjectId, active: false);
        await AssertRejectedAsync(member, "/api/v1/auth/me", UserErrorCodes.LocalAccessDisabled);

        // 机器主体照旧由自然人断言拒绝：403、不带成员错误码
        using var machine = Bearer(host, Token(key, ClientSubject.Format("orders-worker"), tenantId));
        using var rejected = await machine.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.DoesNotContain(UserErrorCodes.LocalAccessDisabled, body, StringComparison.Ordinal);
        Assert.DoesNotContain(UserErrorCodes.LocalMemberMissing, body, StringComparison.Ordinal);
    }
#if (IncludeNotifications || IncludeRealTime)

    [Fact]
    public async Task A_disabled_member_cannot_open_a_new_hub_connection()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        using var session = factory.CreateResourceSession(subjectId, tenantId);
        const string negotiate = ProjectWebApplicationFactory.HubPath + "/negotiate?negotiateVersion=1";
        using (var allowed = await session.Client.PostAsync(negotiate, null))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        await SetActiveAsync(factory.Services, tenantId, subjectId, active: false);

        await AssertRejectedAsync(session.Client, negotiate, UserErrorCodes.LocalAccessDisabled, post: true);
    }
#endif
#if (IncludeRealTime)

    /// <summary>已建立的连接在下一次方法调用时按同一策略复评，被中止。</summary>
    /// <remarks>
    /// <para>用 <c>Unsubscribe</c> 探测：它不经订阅授权、对任何已连接主体都放行，被拒只可能来自策略复评。</para>
    /// <para>必须走 WebSocket：长轮询的每次收发都是新的 HTTP 请求，会先被 HTTP 层的成员策略拒掉，
    /// 测不到 Hub 调用作用域里（无快照、按主键读状态）的复评。</para>
    /// </remarks>
    [Fact]
    public async Task A_disabled_member_is_cut_off_at_the_next_hub_invocation()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        using var session = factory.CreateResourceSession(subjectId, tenantId);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, ProjectWebApplicationFactory.HubPath), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = factory.Server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        foreach (var (name, value) in session.AuthenticationHeaders)
                            request.Headers[name] = value;
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync();
        await connection.InvokeAsync("Unsubscribe", "any");

        await SetActiveAsync(factory.Services, tenantId, subjectId, active: false);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync("Unsubscribe", "any"));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
#endif

    // 计数的探针：只要求当前用户、处理本身不查用户表（读设置会按权限判定可管理项，RBAC 主体解析每请求查库），
    // 计到的用户表读取因此只来自投影与授权阶段
    private static Task<HttpResponseMessage> PutOwnTimeZoneAsync(HttpClient client)
        => client.PutAsJsonAsync("/api/v1/settings/current-user",
            new { Name = SettingConstant.Display.TimeZone, Value = "Asia/Tokyo" });

    private static async Task AssertRejectedAsync(HttpClient client, string path, string code, bool post = false)
    {
        using var response = post ? await client.PostAsync(path, null) : await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    private static async Task SetActiveAsync(IServiceProvider services, Guid? tenantId, Guid userId, bool active)
    {
        await using var scope = services.CreateAsyncScope();
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var user = await db.Set<User>().SingleAsync(user => user.Id == userId);
            if (active) user.Enable();
            else user.Disable();
            await db.SaveChangesAsync();
        }
    }

    private static async Task GrantAsync(IServiceProvider services, Guid? tenantId, Guid userId, string permission)
    {
        await using var scope = services.CreateAsyncScope();
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            await scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>()
                .GrantAsync(permission, PermissionGrantProviderNames.User, userId.ToString());
        }
    }

    private static HttpClient Bearer(WebApplicationFactory<Program> host, string token)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Token(SecurityKey key, string subject, Guid? tenantId)
    {
        var claims = new Dictionary<string, object> { [CustomClaimTypes.Subject] = subject, ["jti"] = Guid.NewGuid().ToString() };
        if (tenantId is { } tenant) claims[CustomClaimTypes.TenantId] = tenant.ToString();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Claims = claims, TokenType = "at+jwt",
            IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-5), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }

    // 只拦插入这个主体的提交：投影建不成行
    private sealed class FailingUserInsert(Guid subjectId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<User>()
                    .Any(entry => entry.State == EntityState.Added && entry.Entity.Id == subjectId) == true)
                throw new InvalidOperationException("Simulated projection failure.");

            return ValueTask.FromResult(result);
        }
    }

    // 只数（或让其失败）以这个主体为参数、读用户表的查询：别的请求、后台任务与投影的资料刷新写入不计入
    private sealed class SubjectQueryCounter(Guid subjectId) : DbCommandInterceptor
    {
        private int count;

        public bool Fail { get; init; }

        public int Count => Volatile.Read(ref count);

        public void Reset() => Interlocked.Exchange(ref count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"Users\"", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, subjectId)))
            {
                Interlocked.Increment(ref count);
                if (Fail)
                    throw new InvalidOperationException("Simulated database failure.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
#endif
