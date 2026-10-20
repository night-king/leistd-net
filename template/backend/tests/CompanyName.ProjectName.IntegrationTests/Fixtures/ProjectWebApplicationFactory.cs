#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using Leistd.MultiTenancy.Context;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.UnitOfWork;
#endif
#if (RemoteTokenAuth)
using CompanyName.ProjectName.Api.HealthChecks;
using CompanyName.ProjectName.Api.HostedServices.Initializer;
#endif
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if (!LocalIdentity)
using System.Security.Claims;
using System.Text.Encodings.Web;
using CompanyName.ProjectName.Api.Auth.Authorization;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
#if (IncludeMultiTenancy)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#endif
#if (!IncludeOperationRecords)
using Serilog.Core;
#endif

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>集成测试宿主：每个实例一份从已迁移模板库克隆出的 PostgreSQL 库（见 <see cref="PostgreSqlTestDatabase"/>）。</summary>
/// <remarks>
/// <para>走生产的 Npgsql 注册路径：唯一约束、查询翻译、事务与独立事务都与生产一致。
/// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/> 派生的宿主共用父实例的库。</para>
/// <para>本夹具一个服务一个库。控制库与业务库拆到不同实例、DbMigrator 命令行、跨进程共用的密钥环
/// 属于部署形态，在部署环境验收。</para>
/// </remarks>
public sealed class ProjectWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>测试宿主的超级管理员密码。唯一定义处。</summary>
    /// <remarks>
    /// 以字面量散在各调用点时，改动测试凭据要逐处追平；集中之后是改一行。
    /// 取值要满足密码策略（见 <c>PasswordPolicy</c>），否则测试宿主自己就起不来。
    /// </remarks>
    public const string TestAdminPassword = "IntegrationTests!Adm1n";
#if (IncludeNotifications || IncludeRealTime)

    /// <summary>客户端连接的 Hub：有业务实时时是实时 Hub（通知也经它推送），否则是通知自己的 Hub。</summary>
#if (IncludeRealTime)
    public const string HubPath = "/hubs/realtime";
#else
    public const string HubPath = "/hubs/notifications";
#endif
#endif

#if (SpaFrontend)
    /// <summary>
    /// 测试宿主（非 Development 环境）的会话 Cookie 名。写明期望值而不是读取实现的配置，
    /// 前缀一旦被改掉，所有取会话 Cookie 的用例都会失败。
    /// </summary>
    public const string SessionCookieName = "__Host-Http-CompanyName.ProjectName.Auth";

    /// <summary>
    /// 校验签发会话 Cookie 的 Set-Cookie 满足 <c>__Host-Http-</c> 前缀的全部条件：
    /// 浏览器对不满足的同名 Cookie 直接丢弃，只看名字测不出来。
    /// </summary>
    public static string AssertSessionCookieContract(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(SessionCookieName + "=", StringComparison.Ordinal));
        var attributes = header.Split(';').Skip(1).Select(part => part.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("secure", attributes);
        Assert.Contains("httponly", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain=", StringComparison.Ordinal));
        return header.Split(';', 2)[0];
    }
#endif

#if (RemoteTokenAuth)
    // 协议测试保留生产认证与自然人策略，其他业务用例使用专用主体替身。
    internal bool UseProductionAuthentication { get; init; }
#endif

    private string? connectionString;
#if (SpaFrontend)

    // 每个宿主一份密钥目录：开发环境以外 Data Protection 要求显式的持久位置，测试给临时目录
    private readonly string dataProtectionKeysPath = Path.Combine(Path.GetTempPath(), $"ProjectTests-keys-{Guid.NewGuid():N}");
#endif

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 专用环境而不是 Development：开发环境会自动加载开发者本机的 user-secrets 与 appsettings.Development.json，
        // 测试结果就随机器变化。测试需要的配置全部在下面显式给出
        builder.UseEnvironment("Testing");

        // 注册阶段就被读取的键（选锁与缓存实现、密钥位置）必须经 UseSetting 注入：
        // 下面 ConfigureAppConfiguration 加的配置在 Program 注册服务时还不可见，
        // 放在那里会被静默忽略——测试宿主会按开发机的环境变量去连 Redis。
        // 派生宿主会再次进入本方法，复用同一个库
        connectionString ??= PostgreSqlTestDatabase.CreateDatabase();
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("ConnectionStrings:Redis", "");
#if (SpaFrontend)
        builder.UseSetting("DataProtection:KeysPath", dataProtectionKeysPath);
#endif
#if (OpenIddictServer)
        builder.UseSetting("OAuth:UseDevelopmentCertificates", "true");
#endif
#if (RemoteTokenAuth)
        // 启动期校验的签发方地址：基线配置刻意留空，缺失即启动失败
        builder.UseSetting("Authentication:Issuer", "https://identity.test/");
#if (ResourceBrowserSession)
        builder.UseSetting("Authentication:ClientId", "resource-test");
        builder.UseSetting("Authentication:ClientSecret", "resource-test-secret");
#endif
        // 租户连接以机器身份向 Identity 回源：组合与生产一致（含工作负载身份），
        // 测试宿主里没有 Identity，回源存储换成下面的共享库替身，不会真的换令牌
        builder.UseSetting("Leistd:ServiceClients:Identity:BaseAddress", "https://identity.test/");
        builder.UseSetting("Leistd:ServiceAuth:Authority", "https://identity.test/");
        builder.UseSetting("Leistd:ServiceAuth:ClientId", "resource-test-worker");
        builder.UseSetting("Leistd:ServiceAuth:ClientSecret", "resource-test-worker-secret");
#endif

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DefaultAdmin:Username"] = "admin",
                ["DefaultAdmin:Password"] = TestAdminPassword,
                // 每个宿主都要播种管理员、每次登录都要校验口令；生产工作因子让这两步占去集成测试的大半 CPU。
                // 哈希格式与默认值的契约由 PasswordHashingTests 按生产默认值验证
                ["PasswordHash:IterationCount"] = "1000",
#if (Email)
                // 固定值即可：测试要的是确定性，不是保密性
                ["Leistd:Security:VerificationCodes:Key"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                ["UserRegistration:EnableEmailVerification"] = "false",
#endif
            });
        });

        // 测试宿主关闭确定化：强制托管服务顺序启停（关闭并发路径），避免 WebApplicationFactory 释放时
        // Host.ForeachService 与已释放的 CancellationTokenSource 竞争，导致类清理期偶发 ObjectDisposedException。
        // 作用于工厂 → 覆盖所有测试类（Health / Localization / Notifications 等），而非逐类修补。
        builder.ConfigureServices(services =>
        {
#if (!IncludeOperationRecords)
            services.AddSingleton<OperationRecordLogCapture>();
            services.AddSingleton<ILogEventSink>(sp => sp.GetRequiredService<OperationRecordLogCapture>());
#endif
#if (RemoteTokenAuth)
            // 测试宿主里没有真实 Identity，启动探针永远探不通。这里直接把门禁置为已开：
            // 其它用例要测的是业务端点，不是"等 Identity 就绪"这件事。
            // 门禁本身的语义（未确认前拒绝流量、确认后锁存）由 ResourceReadinessGateTests 单独钉住
            // 只摘这一个托管服务：RemoveAll<IHostedService>() 会把 ApplicationInitializer
            // 一起摘掉，那是其它用例赖以初始化的东西
            var probe = services.SingleOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(RemoteIdentityReadinessInitializer));
            if (probe is not null)
            {
                services.Remove(probe);
            }

            var openedGate = new RemoteIdentityReadinessHealthCheck();
            openedGate.MarkReady();
            services.AddSingleton(openedGate);

#if (IncludeMultiTenancy)
            services.RemoveAll<ITenantConnectionConfigurationStore>();
            services.RemoveAll<ITenantDatabaseDirectory>();
            services.AddSingleton<SharedTenantConnectionStore>();
            services.AddSingleton<ITenantConnectionConfigurationStore>(sp => sp.GetRequiredService<SharedTenantConnectionStore>());
            services.AddSingleton<ITenantDatabaseDirectory>(sp => sp.GetRequiredService<SharedTenantConnectionStore>());
#endif
#endif
#if (!LocalIdentity)
            if (!UseProductionAuthentication)
            {
                // Resource 模板不托管登录端点。集成测试以专用方案注入已验证主体，
                // 不伪造生产 Bearer 验签，也不让 Resource 回退为本地 Cookie 登录。
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ResourceTestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = ResourceTestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ResourceTestAuthenticationHandler>(
                        ResourceTestAuthenticationHandler.SchemeName,
                        _ => { });
                // 只把策略的认证方案换成替身，要求保持生产原样（自然人、本地成员未停用）：
                // 换成"只要求已认证"的替身策略，成员停用这类判定在测试里会静默失效。
                // PostConfigure 排在生产的 Configure 之后，读到的是生产已建好的默认策略
                services.PostConfigure<AuthorizationOptions>(options =>
                {
                    var testPolicy = new AuthorizationPolicy(
                        options.DefaultPolicy.Requirements,
                        [ResourceTestAuthenticationHandler.SchemeName]);
                    options.DefaultPolicy = testPolicy;
                    // 组件端点按名字要这条策略（不套默认策略），替身方案必须把它一起换掉，
                    // 否则通知中心、读设置这些自用端点仍然要求生产 Bearer，测试里一律 401
                    options.AddPolicy(ApiPolicies.CurrentUser, testPolicy);
                });
            }
#endif
            services.Configure<HostOptions>(options =>
            {
                options.ServicesStartConcurrently = false;
                options.ServicesStopConcurrently = false;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        if (connectionString is not null)
        {
            PostgreSqlTestDatabase.DropDatabase(connectionString);
        }

#if (SpaFrontend)
        if (Directory.Exists(dataProtectionKeysPath))
        {
            Directory.Delete(dataProtectionKeysPath, recursive: true);
        }
#endif
    }

    public HttpClient CreateProjectClient() => CreateProjectClient(this);

    /// <summary>
    /// 供 <see cref="WebApplicationFactory{T}.WithWebHostBuilder"/> 派生出的宿主复用。
    /// 那个方法返回的是基类类型，拿不到本类的实例成员。
    /// </summary>
    public static HttpClient CreateProjectClient(WebApplicationFactory<Program> host)
    {
        return host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

#if (LocalIdentity)
    public Task<AuthenticatedSession> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
        => LoginAsync(this, username, password, cancellationToken);

    public static async Task<AuthenticatedSession> LoginAsync(
        WebApplicationFactory<Program> host,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var client = CreateProjectClient(host);
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = username, Password = password },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]));
        Assert.False(string.IsNullOrWhiteSpace(cookie));
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new AuthenticatedSession(client, cookie);
    }

    /// <summary>经设置写入端直接写宿主（<paramref name="tenantId"/> 为 <see langword="null"/>）或某个租户的租户级设置。</summary>
    /// <remarks>
    /// 不经设置接口：有的设置一写就限制写入者自己的会话（例如"要求两步验证"对没启用的管理员即时生效），
    /// 用管理员会话写入之后连撤回的请求都发不出去。走的仍是写入端（定义校验、作用域缓存作废），下一个请求可见。
    /// </remarks>
    public static async Task SetTenantSettingAsync(
        WebApplicationFactory<Program> host,
        string name,
        string? value,
        Guid? tenantId = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var tenant = services.GetRequiredService<ICurrentTenant>().Change(tenantId);
        using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        await services.GetRequiredService<ISettingManager>().SetAsync(name, value, SettingScopes.Tenant);
        await unitOfWork.CompleteAsync();
    }
#endif

#if (!LocalIdentity)
    /// <summary>新测试主体所属的租户：多租户时是一个新租户，单租户时为 <see langword="null"/>（宿主）。</summary>
    public static Guid? NewTenantId() =>
#if (IncludeMultiTenancy)
        Guid.CreateVersion7();
#else
        null;
#endif

    public AuthenticatedSession CreateResourceSession(Guid subjectId, Guid? tenantId) =>
        CreateResourceSession(this, subjectId, tenantId);

    /// <param name="host">测试宿主。</param>
    /// <param name="subjectId">主体标识。</param>
    /// <param name="tenantId">主体的租户声明；<see langword="null"/> 表示宿主主体（不带租户声明）。</param>
    public static AuthenticatedSession CreateResourceSession(
        WebApplicationFactory<Program> host,
        Guid subjectId,
        Guid? tenantId)
    {
        var headers = new Dictionary<string, string>
        {
            [ResourceTestAuthenticationHandler.SubjectHeader] = subjectId.ToString()
        };
        if (tenantId is { } tenant)
            headers[ResourceTestAuthenticationHandler.TenantHeader] = tenant.ToString();
        var client = CreateProjectClient(host);
        foreach (var (name, value) in headers)
            client.DefaultRequestHeaders.Add(name, value);

        return new AuthenticatedSession(client, string.Empty, headers);
    }
#endif
}

public sealed class AuthenticatedSession(
    HttpClient client,
    string cookie,
    IReadOnlyDictionary<string, string>? authenticationHeaders = null) : IDisposable
{
    public HttpClient Client { get; } = client;
    public string Cookie { get; } = cookie;
    public IReadOnlyDictionary<string, string> AuthenticationHeaders { get; } =
        authenticationHeaders ?? new Dictionary<string, string>();

    public void Dispose() => Client.Dispose();
}

#if (!LocalIdentity)
/// <summary>远端租户连接存储的替身：所有租户都没有登记独立连接，落在宿主的共享库上。</summary>
/// <remarks>
/// 回源协议本身（缓存分区、取消不牵连搭车者）按生产注册方式单独装配后测试；
/// 这里只让业务用例在没有 Identity 的测试宿主里拿到确定的路由。
/// </remarks>
internal sealed class SharedTenantConnectionStore : ITenantConnectionConfigurationStore, ITenantDatabaseDirectory
{
    public Task<TenantConnectionLookupResult?> FindAsync(Guid tenantId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<TenantConnectionLookupResult?>(new TenantConnectionLookupResult { TenantId = tenantId, HasAnyConnection = false });

    public Task<TenantMigrationConnectionListResult> GetListAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TenantMigrationConnectionListResult([], []));

    public Task<TenantDatabaseListResult> GetDatabasesAsync(string name, bool activeOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult(TenantDatabaseListResult.Empty);
}

internal sealed class ResourceTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ProjectTests";
    public const string SubjectHeader = "X-Project-Test-Subject";
    public const string TenantHeader = "X-Project-Test-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers[SubjectHeader].SingleOrDefault();
        if (!Guid.TryParse(subject, out var subjectId))
            return Task.FromResult(AuthenticateResult.NoResult());

        // 租户头缺省即宿主主体；给了就原样写成租户声明（含非法值），由被测宿主自己判定
        var claims = new List<Claim> { new("sub", subjectId.ToString()) };
        foreach (var tenant in Request.Headers[TenantHeader])
            claims.Add(new Claim(CustomClaimTypes.TenantId, tenant!));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
#endif
