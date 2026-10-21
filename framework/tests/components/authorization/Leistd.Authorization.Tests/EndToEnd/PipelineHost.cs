using Leistd.Security.AspNetCore;
using Leistd.Authorization.Resource.AspNetCore;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Leistd.Authorization.AspNetCore;
using Leistd.Authorization.DataScope;
using Leistd.Authorization.EntityFrameworkCore;
using Leistd.Authorization.Resource.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Resource.Grants;
using Leistd.Authorization.DataScope.Abstractions;

namespace Leistd.Authorization.Tests.EndToEnd;

/// <summary>真实 ASP.NET Core 宿主：验证三层授权串起来之后的行为。</summary>
/// <remarks>
/// 单元测试只能覆盖每一层自己的语义，覆盖不到集成缝：动态 Policy 是否真的接上了检查器、
/// 数据范围能否作用在仓储返回的 IQueryable 上、资源判定是否发生在实体加载之后、
/// 以及三者的先后顺序是否正确。这些正是最容易写错、且一旦写错就是安全缺陷的地方。
/// </remarks>
public sealed class PipelineHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WebApplication _app;

    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;

    private PipelineHost(SqliteConnection connection, WebApplication app, HttpClient client)
    {
        _connection = connection;
        _app = app;
        Client = client;
    }

    public static async Task<PipelineHost> StartAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Host.UseServiceProviderFactory(new DynamicProxyServiceRegistrationCallbackFactory());
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();

        // CreateSlimBuilder 不默认注册它；主体提供器要从请求上下文取当前用户。
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDbContext<PipelineDbContext>(options => options.UseSqlite(connection));

        // 认证：用请求头模拟已登录主体，把注意力留给授权本身。
        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            // 显式注册一个与权限同名、但更严格的策略：除权限外还要求一个 Claim。
            // 用来验证动态权限策略不会把宿主自己注册的同名策略盖掉。
            options.AddPolicy(
                OrderPermissions.Approve,
                policy => policy.RequireClaim(PipelineFixtures.ApprovalClaim));
        });

        // 工作单元 + EF 提供方：授权与资源 ACL 的 EF 存储经 IDbContextProvider 取上下文
        // （只有它会设置 DbContextCreationContext.Current），因此这是宿主必须自行注册的前置，
        // 与 AddMultiTenancyEfCore 同一约定——组件不替其它组件注册基础设施。
        builder.Services.AddUnitOfWork();
        builder.Services.AddUnitOfWorkEfCore();

        // 第一层：功能权限。AddPermissionAuthorization 让 [Authorize(Policy = "权限名")] 生效。
        builder.Services.AddPermissionAuthorization();
        builder.Services.AddPermissionAuthorizationEfCore<PipelineDbContext>();
        builder.Services.AddSingleton<IPermissionDefinitionProvider, OrderPermissionDefinitionProvider>();
        builder.Services.AddScoped<IPermissionSubjectProvider, TestPermissionSubjectProvider>();

        // 第二层：数据范围。
        builder.Services.AddDataScopeCore();
        builder.Services.AddScoped<IDataScopeAssignmentProvider, TestDataScopeAssignmentProvider>();
        builder.Services.AddDataScopeProvider<Order, OwnOrderScopeProvider>();
        builder.Services.AddDataScopeProvider<Order, OrganizationOrderScopeProvider>();

        // 第三层：资源实例授权。
        builder.Services.AddResourceAuthorizationEfCore<PipelineDbContext>();
        builder.Services.AddSecurity();
        builder.Services.AddResourceAuthorization();
        builder.Services.AddScoped<IAuthorizationHandler, OrderOwnerHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, ArchivedOrderHandler>();

        builder.Services.AddScoped<OrderAppService>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        OrderEndpoints.Map(app);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await app.StartAsync();
        return new PipelineHost(connection, app, app.GetTestClient());
    }

    /// <summary>以指定主体发起请求。</summary>
    public HttpRequestMessage Request(HttpMethod method, string url, string userId, params string[] roleIds)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, userId);
        if (roleIds.Length > 0)
        {
            request.Headers.Add(TestAuthenticationHandler.RolesHeader, string.Join(',', roleIds));
        }

        return request;
    }

    public async Task ExecuteAsync(Func<PipelineDbContext, Task> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<PipelineDbContext>());
    }

    public async Task GrantAsync(string providerName, string providerKey, params string[] permissions)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>();
        foreach (var permission in permissions)
        {
            await manager.GrantAsync(permission, providerName, providerKey);
        }
    }

    public async Task ReplaceResourceGrantsAsync(
        string resourceKey,
        params ResourceGrant[] grants)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IResourceGrantManager>();
        await manager.ReplaceGrantsAsync(Order.Resource, resourceKey, grants);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>把请求头里的用户与角色变成已认证主体。</summary>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";
    public const string ClaimsHeader = "X-Test-Claims";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userId) || string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId!) };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        if (Request.Headers.TryGetValue(ClaimsHeader, out var extraClaims))
        {
            claims.AddRange(extraClaims.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(claim => new Claim(claim, "true")));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class TestPermissionSubjectProvider(IHttpContextAccessor accessor) : IPermissionSubjectProvider
{
    public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default)
        => accessor.HttpContext?.User is { } user
            ? GetSubjectAsync(user, cancellationToken)
            : Task.FromResult<PermissionSubject?>(null);

    public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult<PermissionSubject?>(null);
        }

        var roleIds = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray();
        var isSuperAdmin = user.HasClaim(PipelineFixtures.SuperAdminClaim, "true");

        return Task.FromResult<PermissionSubject?>(new PermissionSubject(userId, roleIds, isSuperAdmin));
    }
}

/// <summary>范围分配：读取整个组织，修改只允许本人。</summary>
/// <remarks>刻意让读写范围不同，用来验证"能看不等于能改"。</remarks>
public sealed class TestDataScopeAssignmentProvider : IDataScopeAssignmentProvider
{
    public ValueTask<IReadOnlyList<DataScopeAssignment>> GetAssignmentsAsync(
        PermissionSubject subject,
        string resourceName,
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (resourceName != Order.Resource || !subject.RoleIds.Contains(PipelineFixtures.OrgRoleId))
        {
            return ValueTask.FromResult<IReadOnlyList<DataScopeAssignment>>([]);
        }

        IReadOnlyList<DataScopeAssignment> assignments = operation switch
        {
            DataOperations.Update =>
            [
                new DataScopeAssignment(Order.Resource, DataOperations.Update, OwnOrderScopeProvider.Scope)
            ],
            _ =>
            [
                new DataScopeAssignment(
                    Order.Resource,
                    operation,
                    OrganizationOrderScopeProvider.Scope,
                    PipelineFixtures.OrganizationId)
            ],
        };

        return ValueTask.FromResult(assignments);
    }
}

public static class PipelineFixtures
{
    public const string OrgRoleId = "role-org";
    public const string OrganizationId = "org-1";
    public const string OwnerUserId = "u-owner";
    public const string ColleagueUserId = "u-colleague";
    public const string OutsiderUserId = "u-outsider";

    /// <summary>宿主显式注册的 Approve 策略额外要求的 Claim。</summary>
    public const string ApprovalClaim = "order-approval";

    /// <summary>标记主体为超级管理员的 Claim。</summary>
    public const string SuperAdminClaim = "super-admin";
}
