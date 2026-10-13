using Leistd.MultiTenancy.AspNetCore.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using Microsoft.Extensions.Options;
using Leistd.BackgroundJobs.Recurring;
using CompanyName.ProjectName.Domain.Auth.Options;
using CompanyName.ProjectName.Application.Auth.BackgroundJobs;
using CompanyName.ProjectName.Application.Tenants.Provider;
using CompanyName.ProjectName.Application.Tenants.Dtos;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using CompanyName.ProjectName.Infrastructure.Persistence;
using CompanyName.ProjectName.Infrastructure.Persistence.EntityConfigurations;
using Npgsql;
using Leistd.Authorization.Errors;
using Leistd.Ddd.Domain.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Leistd.MultiTenancy.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Authorization.EntityFrameworkCore.Entities;
#if (IncludeOperationRecords)
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
#endif
using Leistd.Settings.EntityFrameworkCore.Entities;
using Leistd.MultiTenancy.Management.Provisioning;
#if (IncludeNotifications)
using Leistd.Notifications.EntityFrameworkCore.Entities;
#endif
using Leistd.MultiTenancy.EntityFrameworkCore.Managers;
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Errors;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Tenancy;
using Leistd.Data.Paging;
#if (ExternalLogin)
using CompanyName.ProjectName.Domain.Auth.ValueObjects;
using Leistd.Timing;
#endif
using Leistd.UnitOfWork;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Users.DomainServices;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>多租户端到端闭环：建租户即种子、租户登录、数据硬隔离、宿主侧权限边界、停用即拒。</summary>
public sealed class TenancyTests : IClassFixture<ProjectWebApplicationFactory>, IDisposable
{
    private readonly ProjectWebApplicationFactory _factory;
    private readonly List<IDisposable> _disposables = [];

    public TenancyTests(ProjectWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private async Task<AuthenticatedSession> LoginHostAdminAsync()
    {
        var session = await _factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        _disposables.Add(session);
        return session;
    }

    /// <summary>宿主管理员建租户，返回租户 Id。</summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task AssertTenantAbsentAsync(WebApplicationFactory<Program> host, string name)
    {
        using var scope = host.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<IdentityControlDbContext>();
        var normalized = scope.ServiceProvider.GetRequiredService<ITenantNormalizer>().NormalizeName(name)!;

        Assert.False(await control.Set<TenantRecord>()
            .IgnoreQueryFilters()
            .AnyAsync(record => record.NormalizedName == normalized && !record.IsDeleted));
    }

    private async Task<Guid> CreateTenantAsync(AuthenticatedSession hostAdmin, string name)
    {
        var response = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = $"{name} Inc.",
            AdminEmail = $"admin@{name.ToLowerInvariant()}.example.com",
            AdminPassword = "Tenant@123456"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>租户管理员登录：登录请求与后续请求都携带租户提示头。</summary>
    private Task<HttpClient> LoginTenantAdminAsync(Guid tenantId, string password = "Tenant@123456")
        => LoginTenantAdminAsync(_factory, tenantId, password);

    /// <summary>在指定宿主上做租户管理员登录（<c>WithWebHostBuilder</c> 派生的宿主用这个重载）。</summary>
    private async Task<HttpClient> LoginTenantAdminAsync(
        WebApplicationFactory<Program> host, Guid tenantId, string password = "Tenant@123456")
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        _disposables.Add(client);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "admin", Password = password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]));
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    /// <summary>以匿名身份带租户头调用注册端点，返回状态码。</summary>
    /// <remarks>
    /// 注册是匿名端点，正是"半成品租户"最现实的入侵面：租户一旦启用，
    /// 任何人带上它的租户提示头 就能在还没有管理员的租户里注册出第一个用户。
    /// </remarks>
    private static async Task<HttpStatusCode> ProbeAnonymousRegistrationAsync(
        WebApplicationFactory<Program> host, Guid tenantId)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            Username = "intruder",
            Email = "intruder@example.com",
            Password = "Intruder@123456"
        });

        return response.StatusCode;
    }

    private static async Task<List<string>> GetUsernamesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/users?offset=0&limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("username").GetString()!)
            .ToList();
    }

    /// <summary>租户管理员是<b>普通用户 + Admin 角色</b>，不是超管。</summary>
    /// <remarks>
    /// <para><c>IsSuperAdmin</c> 是宿主的防锁死逃生舱：旁路功能权限、资源实例授权与数据范围过滤，
    /// 且不可停用、不可删除、没有撤销入口。给租户用户打上它会带来三个真实后果——</para>
    /// <para>1. 任何直接读 <c>is_super_admin</c> claim、不经权限检查器的判定点
    /// （自定义授权策略就是这种）会失去侧别边界，成为跨租户越权；</para>
    /// <para>2. 该用户在自己租户内无法被停用或删除；</para>
    /// <para>3. 数据范围与资源实例授权在租户内被整体旁路。</para>
    /// <para>租户不需要逃生舱：Admin 角色已带全部 Tenant 侧权限（含权限管理本身），
    /// 升级后新增的权限由租户管理员自己授给自己的角色。</para>
    /// </remarks>
    [Fact]
    public async Task Tenant_admin_is_not_super_admin_and_gets_permissions_from_the_admin_role()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "roleonly");

        using var scope = _factory.Services.CreateScope();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        using (currentTenant.Change(tenantId))
        {
            var users = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
            var tenantAdmin = await users.GetFirstAsync(u => u.Username == "admin");

            Assert.NotNull(tenantAdmin);
            Assert.False(tenantAdmin.IsSuperAdmin);

            Assert.True(tenantAdmin.CanBeDisabled());
            Assert.True(tenantAdmin.CanBeDeleted());

            var withRoles = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetWithRolesAsync(tenantAdmin.Id);
            Assert.NotEmpty(withRoles!.GetRoleIds());
        }
    }

    /// <summary>租户管理员能行使 Tenant 侧权限，但取不到任何宿主侧权限</summary>
    [Fact]
    public async Task Tenant_admin_has_tenant_permissions_but_no_host_permissions()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "hostsideguard");
        var tenantClient = await LoginTenantAdminAsync(tenantId);

        var roles = await tenantClient.GetAsync("/api/v1/roles?offset=0&limit=10");
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);

        var tenants = await tenantClient.GetAsync("/api/v1/tenants?offset=0&limit=10");
        Assert.Equal(HttpStatusCode.Forbidden, tenants.StatusCode);
    }

    /// <summary>领域服务拒绝在租户上下文里造超管</summary>
    /// <remarks>
    /// 这一条只覆盖领域服务这道关；数据库那道兜底见
    /// <see cref="Database_refuses_to_move_a_super_admin_into_a_tenant"/>。
    /// </remarks>
    [Fact]
    public async Task Domain_service_refuses_to_create_a_super_admin_in_a_tenant()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "noescape");

        using var scope = _factory.Services.CreateScope();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var users = scope.ServiceProvider
            .GetRequiredService<UserDomainService>();

        using (currentTenant.Change(tenantId))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                users.CreateSuperAdminAsync(
                    "escape-hatch",
                    "escape@example.com",
                    "Tenant@123456",
                    displayName: null,
                    passwordSubject: "test"));

            Assert.Contains(tenantId.ToString(), error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>数据库拒绝把超管挪进租户</summary>
    /// <remarks>
    /// 检查约束 <c>CK_User_SuperAdminIsHostOnly</c> 挡的是不经领域入口的写入：数据修复脚本、批量导入、直接 SQL。
    /// 用宿主种子产出的真实超管，不另造行。
    /// </remarks>
    [Fact]
    public async Task Database_refuses_to_move_a_super_admin_into_a_tenant()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        // 确有宿主超管行，否则下面的 UPDATE 命中 0 行，会假装"约束生效"
        Assert.True(await db.Set<User>().IgnoreQueryFilters().AnyAsync(user => user.IsSuperAdmin && user.TenantId == null));

        var users = db.Model.FindEntityType(typeof(User))!;
        var sql = $"UPDATE \"{users.GetSchema()}\".\"{users.GetTableName()}\" " +
            $"SET \"{nameof(User.TenantId)}\" = {{0}} WHERE \"{nameof(User.IsSuperAdmin)}\"";
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(UserCheckConstraints.SuperAdminIsHostOnly, error.ConstraintName);
    }

    /// <summary>租户内被业务规则拒绝的写操作，失败记录留在该租户。</summary>
    /// <remarks>
    /// 失败记录须在租户作用域退出前冻结归属，避免写入宿主层。
    /// </remarks>
    [Fact]
    public async Task A_rejected_permission_save_inside_a_tenant_is_recorded_in_that_tenant()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "failaudit");
        var tenantClient = await LoginTenantAdminAsync(tenantId);

        using var roles = JsonDocument.Parse(await tenantClient.GetStringAsync("/api/v1/roles?offset=0&limit=50"));
        var roleId = roles.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == AdminConstant.RoleName)
            .GetProperty("id").GetGuid();

        var stale = await tenantClient.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{roleId}",
            new { expectedVersion = 999, permissionNames = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var target = $"Role/{roleId}";
        var inTenant = await OperationRecordQueries.GetFailuresAsync(
            _factory, tenantClient, OperationRecordActions.PermissionGrantsReplaced, target, tenantId);
        Assert.Equal(PermissionErrorCodes.ConcurrencyConflict, Assert.Single(inTenant).FailureCode);
        Assert.Empty(await OperationRecordQueries.GetFailuresAsync(
            _factory, hostAdmin.Client, OperationRecordActions.PermissionGrantsReplaced, target));
    }

    /// <summary>过期会话清理作业也删掉共享库里租户用户的过期会话。</summary>
    /// <remarks>
    /// 作业按物理库逐个执行，同一个库里有多个租户；删除时不关租户过滤，宿主上下文里只看得到宿主的会话，
    /// 共享库租户的过期会话会永远留下。
    /// </remarks>
    [Fact]
    public async Task The_session_cleanup_job_reaches_tenant_users_in_the_shared_database()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "sesscleanup");
        var tenantClient = await LoginTenantAdminAsync(tenantId);
        using var me = JsonDocument.Parse(await tenantClient.GetStringAsync("/api/v1/auth/me"));
        var userId = me.RootElement.GetProperty("id").GetGuid();

        Guid expiredId;
        using (var scope = _factory.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            var idleTimeout = scope.ServiceProvider.GetRequiredService<IOptions<UserSessionOptions>>().Value.IdleTimeout;
            using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
            var session = await scope.ServiceProvider.GetRequiredService<IRepository<UserSession, Guid>>().InsertAsync(
                new UserSession(userId, DateTime.UtcNow - idleTimeout - TimeSpan.FromDays(1), "203.0.113.9", "stale device"));
            await unitOfWork.CompleteAsync();
            expiredId = session.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ExpiredUserSessionCleanupJob>().ExecuteAsync(
                new RecurringJobContext(ExpiredUserSessionCleanupJob.Name, DateTimeOffset.UtcNow), CancellationToken.None);
        }

        using (var scope = _factory.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IRepository<UserSession, Guid>>().GetByIdAsync(expiredId));
        }
    }

    [Fact]
    public async Task Created_tenant_is_seeded_and_its_admin_sees_only_its_users()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "acme");

        // 按名字查租户的匿名端点已被移除（它是租户存在性 oracle）；这里用已认证的按 id 查询
        var lookup = await hostAdmin.Client.GetAsync($"/api/v1/tenants/{tenantId}");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);

        var tenantClient = await LoginTenantAdminAsync(tenantId);
        var usernames = await GetUsernamesAsync(tenantClient);

        // 只见本租户种子的 admin，见不到宿主 admin（同名但分属不同分区）
        Assert.Contains("admin", usernames);
        Assert.Single(usernames);
    }

    [Fact]
    public async Task Host_sees_only_host_users_and_a_forged_tenant_header_is_ignored()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "isolation-a");

        var hostUsernames = await GetUsernamesAsync(hostAdmin.Client);
        Assert.Contains("admin", hostUsernames);
        Assert.DoesNotContain(hostUsernames, name => name.StartsWith("isolation"));

        // 已认证会话携带伪造租户头：claim 定案为宿主，头无法把会话挪进租户
        using var forged = _factory.CreateProjectClient();
        forged.DefaultRequestHeaders.Add("Cookie", hostAdmin.Cookie);
        forged.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        var forgedUsernames = await GetUsernamesAsync(forged);
        Assert.Equal(hostUsernames.Order(), forgedUsernames.Order());
    }

    [Fact]
    public async Task Cross_tenant_lookup_by_id_returns_404()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantAId = await CreateTenantAsync(hostAdmin, "cross-a");
        var tenantBId = await CreateTenantAsync(hostAdmin, "cross-b");

        var tenantAClient = await LoginTenantAdminAsync(tenantAId);
        var tenantBClient = await LoginTenantAdminAsync(tenantBId);

        var bUsers = await tenantBClient.GetAsync("/api/v1/users?offset=0&limit=10");
        using var bBody = JsonDocument.Parse(await bUsers.Content.ReadAsStringAsync());
        var bAdminId = bBody.RootElement.GetProperty("items").EnumerateArray().First()
            .GetProperty("id").GetGuid();

        // 租户 A 管理员按 Id 访问租户 B 的用户：过滤器使其不可见，统一表现为 404
        var crossDetail = await tenantAClient.GetAsync($"/api/v1/users/{bAdminId}");
        Assert.Equal(HttpStatusCode.NotFound, crossDetail.StatusCode);
    }

    [Fact]
    public async Task Tenant_admin_current_permissions_exclude_host_permissions()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "sidecheck");
        var tenantClient = await LoginTenantAdminAsync(tenantId);

        // 租户管理员的 current 权限集与检查器同口径：宿主侧权限不下发，
        // 前端菜单/路由据此裁剪，不会出现点进去必然 403 的宿主功能
        var current = await tenantClient.GetAsync("/api/v1/permissions/current");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var body = await current.Content.ReadAsStringAsync();
        Assert.DoesNotContain("App.Tenants", body);
        Assert.Contains("App.Users", body);

        var hostCurrent = await hostAdmin.Client.GetAsync("/api/v1/permissions/current");
        Assert.Contains("App.Tenants", await hostCurrent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tenant_admin_cannot_reach_tenant_management_api()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "boundary");

        var tenantClient = await LoginTenantAdminAsync(tenantId);

        var list = await tenantClient.GetAsync("/api/v1/tenants?offset=0&limit=10");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        var create = await tenantClient.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "evil",
            AdminEmail = "evil@example.com",
            AdminPassword = "TenancyTests!Evil"
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Deactivated_tenant_rejects_existing_sessions_and_new_sign_ins()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "frozen");

        var tenantClient = await LoginTenantAdminAsync(tenantId);
        using var navigationSession = await LoginTenantAdminAsync(tenantId);
        Assert.Single(await GetUsernamesAsync(tenantClient));

        // 停用在提交那一刻生效（存储直接读库），在途会话的下一个请求就被拒
        var deactivate = await hostAdmin.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/activation", new { IsActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        // XHR/API 请求：会话自恢复中间件注销 Cookie 并回 401（前端按会话失效清理本地状态）
        var afterDeactivation = await tenantClient.GetAsync("/api/v1/users?offset=0&limit=10");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDeactivation.StatusCode);

        // 带租户失效标记：前端据此把它与"普通会话过期"区分开，只在这种 401 上清掉已选租户。
        // 不带的话前端只能二选一——要么每次超时都强迫重选租户，要么跳回登录页仍带着已死的租户
        Assert.True(afterDeactivation.Headers.Contains("X-Tenant-Invalid"));

        // 反面：普通未认证 401 不带这个头，否则判据失效、前端又会在每次会话过期时清掉租户
        using var anonymous = _factory.CreateProjectClient();
        var ordinary = await anonymous.GetAsync("/api/v1/users?offset=0&limit=10");
        Assert.Equal(HttpStatusCode.Unauthorized, ordinary.StatusCode);
        Assert.False(ordinary.Headers.Contains("X-Tenant-Invalid"));

        // HTML 导航：注销后重定向回原地址，下一次请求已匿名、SPA 可正常加载——用户不会死锁在错误页
        using var navRequest = new HttpRequestMessage(HttpMethod.Get, "/platform/users");
        navRequest.Headers.Accept.ParseAdd("text/html");
        foreach (var header in navigationSession.DefaultRequestHeaders)
        {
            navRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var navClient = _factory.CreateProjectClient();
        var navResponse = await navClient.SendAsync(navRequest);
        Assert.Equal(HttpStatusCode.Redirect, navResponse.StatusCode);

        using var relogin = _factory.CreateProjectClient();
        relogin.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        var loginResponse = await relogin.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "admin", Password = "Tenant@123456" });
        // 匿名带一个已停用的租户，与带未知租户是同一种失败：不暴露它是否存在
        Assert.Equal(HttpStatusCode.NotFound, loginResponse.StatusCode);
    }

    /// <summary>启用一个不存在的租户回 404，不能被"租户内没有用户"的守卫抢先讲成 400。</summary>
    [Fact]
    public async Task Activating_a_missing_tenant_returns_404()
    {
        var hostAdmin = await LoginHostAdminAsync();

        var response = await hostAdmin.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{Guid.NewGuid()}/activation", new { IsActive = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// 租户名须是单个 DNS 标签：按子域名解析时名字就是主机名的一段，<c>Invalid Name!</c> 这类名字
    /// 建得出来却经子域名永远访问不到。经 API 创建与改名都按带码的 400 拒绝，不落库、原名不变。
    /// </summary>
    [Fact]
    public async Task A_tenant_name_that_is_not_a_dns_label_is_a_coded_bad_request()
    {
        var hostAdmin = await LoginHostAdminAsync();

        var create = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "Invalid Name!",
            AdminEmail = "admin@invalid-name.example.com",
            AdminPassword = "Tenant@123456"
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(MultiTenancyErrorCodes.NameInvalid, await ErrorCodeAsync(create));
        await AssertTenantAbsentAsync(_factory, "Invalid Name!");

        var tenantId = await CreateTenantAsync(hostAdmin, "dns-label");
        var rename = await hostAdmin.Client.PutAsJsonAsync($"/api/v1/tenants/{tenantId}", new { Name = "dns-label-" });

        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Equal(MultiTenancyErrorCodes.NameInvalid, await ErrorCodeAsync(rename));
        var detail = await hostAdmin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/tenants/{tenantId}");
        Assert.Equal("dns-label", detail.GetProperty("name").GetString());
    }

    /// <summary>
    /// 名称规则只管新名称：规则之前建出的租户（这里是 64 个字符且带下划线）不改名时照常编辑其他字段，
    /// 端点入参按存储容量 64 接受原名，不因新规则的上限 63 被拒
    /// </summary>
    [Fact]
    public async Task A_pre_rule_tenant_name_can_be_kept_while_editing_other_fields()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "legacy");
        var legacyName = new string('a', 62) + "_x";
        using (var scope = _factory.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<IdentityControlDbContext>();
            var record = await control.Set<TenantRecord>().SingleAsync(t => t.Id == tenantId);
            record.Name = legacyName;
            record.NormalizedName = scope.ServiceProvider.GetRequiredService<ITenantNormalizer>().NormalizeName(legacyName)!;
            await control.SaveChangesAsync();
        }

        var edit = await hostAdmin.Client.PutAsJsonAsync($"/api/v1/tenants/{tenantId}", new { Name = legacyName, DisplayName = "Legacy Inc." });

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var detail = await hostAdmin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/tenants/{tenantId}");
        Assert.Equal(legacyName, detail.GetProperty("name").GetString());
        Assert.Equal("Legacy Inc.", detail.GetProperty("displayName").GetString());
    }

    /// <summary>跨域响应必须把 <c>X-Tenant-Invalid</c> 列入 Access-Control-Expose-Headers。</summary>
    /// <remarks>
    /// CORS 分离部署（前端 dev server 直连后端）是模板明确支持的模式之一。
    /// 自定义响应头不在 CORS 安全清单里，不显式暴露则浏览器不交给 JS——
    /// 后端照常发了头、前端 <c>error.headers.get()</c> 恒为 null，租户失效恢复静默失效。
    /// 这条断言是必要的：后端集成测试直读 TestServer 响应头、前端单测手工构造 HttpHeaders，
    /// 仅断言原始响应头存在会漏掉它；本用例带真实 Origin 并断言 Access-Control-Expose-Headers，
    /// 因此协议层就能发现。浏览器负责的是另一半：前端读到该头、清租户状态、完成跳转的闭环。
    /// </remarks>
    [Fact]
    public async Task Cors_response_exposes_the_tenant_invalid_header()
    {
        // 前端部署在另一个源、经 Cors:AllowedOrigins 放行的形态：默认不放行任何来源时，
        // CORS 中间件不会写任何响应头，这条断言也就无从谈起
        using var corsHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cors:AllowedOrigins:0"] = "http://localhost:4200"
                })));

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(corsHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var tenantId = await CreateTenantAsync(hostAdmin, "cors-check");
        using var tenantClient = await LoginTenantAdminAsync(corsHost, tenantId);

        var deactivate = await hostAdmin.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/activation", new { IsActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users?offset=0&limit=10");
        request.Headers.Add("Origin", "http://localhost:4200");
        foreach (var header in tenantClient.DefaultRequestHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var browserLike = ProjectWebApplicationFactory.CreateProjectClient(corsHost);
        var response = await browserLike.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Tenant-Invalid"));

        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("X-Tenant-Invalid", exposed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>子域名部署下，来自不受信任地址的 X-Forwarded-Host 不能改写租户。</summary>
    /// <remarks>
    /// <para><c>UseForwardedHeaders</c> 会用 <c>X-Forwarded-Host</c> 覆盖 <c>Request.Host</c>，
    /// 而 Host 正是子域名解析的权威来源，且转发头中间件排在多租户中间件之前。
    /// 把 <c>KnownProxies</c>/<c>KnownIPNetworks</c> 清空（=接受任何客户端的转发头）时，
    /// "子域名是匿名请求权威来源"这条边界就形同虚设。</para>
    /// <para>判据用停用租户：把 b 停掉，转发头若被采信就会解析到 b 并 403；
    /// 没被采信则留在 a（或宿主）而 200。用回显不了租户的端点做断言等于什么都没测。</para>
    /// <para>这条必须在模板层：它验证的是宿主管道的组合（转发头信任 × 解析链），
    /// 框架侧那条"请求头改不动子域名"的用例测不到宿主的代理信任配置。</para>
    /// </remarks>
    [Fact]
    public async Task Untrusted_forwarded_headers_cannot_change_the_subdomain_tenant()
    {
        var hostAdmin = await LoginHostAdminAsync();
        await CreateTenantAsync(hostAdmin, "subdomain-a");
        var blockedId = await CreateTenantAsync(hostAdmin, "subdomain-b");

        var deactivate = await hostAdmin.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{blockedId}/activation", new { IsActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        using var domainHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Leistd:MultiTenancy:DomainFormat"] = "{0}.example.com"
                }));

            // TestServer 的客户端地址是环回，而环回在框架默认信任集里——不改的话
            // 转发头会被正常采信，这条用例观测不到目标属性。换成公网测试地址，
            // 模拟"直连的不受信任客户端"
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new UntrustedRemoteAddressFilter()));
        });

        using var client = ProjectWebApplicationFactory.CreateProjectClient(domainHost);

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/by-host");
        forged.Headers.Host = "subdomain-a.example.com";
        forged.Headers.TryAddWithoutValidation("X-Forwarded-Host", "subdomain-b.example.com");

        var response = await client.SendAsync(forged);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var probe = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("tenant", probe.RootElement.GetProperty("decision").GetString());
        Assert.Equal("subdomain-a", probe.RootElement.GetProperty("tenant").GetProperty("name").GetString());
    }

    /// <summary>正向：配置为可信代理的来源，其转发头应当被采信。</summary>
    /// <remarks>
    /// 只有"不可信来源被拒"这一条时，把信任逻辑改成永不生效也能让它保持绿——
    /// 那样网关后的 X-Forwarded-* 全部失效却无人察觉。两个方向都要钉住。
    /// </remarks>
    [Fact]
    public async Task Trusted_proxy_forwarded_headers_are_honored()
    {
        var hostAdmin = await LoginHostAdminAsync();
        await CreateTenantAsync(hostAdmin, "trusted-a");
        var blockedId = await CreateTenantAsync(hostAdmin, "trusted-b");

        var deactivate = await hostAdmin.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{blockedId}/activation", new { IsActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        using var domainHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Leistd:MultiTenancy:DomainFormat"] = "{0}.example.com",
                    ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.10"
                }));

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new UntrustedRemoteAddressFilter()));
        });

        using var client = ProjectWebApplicationFactory.CreateProjectClient(domainHost);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/by-host");
        request.Headers.Host = "trusted-a.example.com";
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "trusted-b.example.com");

        var response = await client.SendAsync(request);

        // 转发头被采信即落到已停用的 trusted-b；匿名下停用与不存在是同一种失败
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>按主机名探测：<b>三档结果必须分开</b>。</summary>
    /// <remarks>
    /// "受管域但不指向租户（宿主）"与"根本不是受管域"在后续解析上语义不同：前者已经定案，
    /// 请求头不再能改写；后者允许记住的租户继续生效。两者都回成"没有租户"时，登录页会在
    /// 宿主域上继续显示上次记住的租户，而这次请求已按宿主处理——界面显示的与实际生效的
    /// 不是同一个租户上下文。这一条只有把两档分开断言才钉得住。
    /// </remarks>
    [Fact]
    public async Task Host_name_probe_distinguishes_tenant_host_and_undecided()
    {
        var hostAdmin = await LoginHostAdminAsync();
        await CreateTenantAsync(hostAdmin, "byhost-a");

        using var domainHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Leistd:MultiTenancy:DomainFormat"] = "{0}.example.com"
                })));

        using var client = ProjectWebApplicationFactory.CreateProjectClient(domainHost);

        async Task<JsonDocument> ProbeAsync(string host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/by-host");
            request.Headers.Host = host;
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        // 租户子域：定案到该租户，并回显它的标识
        using var tenantProbe = await ProbeAsync("byhost-a.example.com");
        Assert.Equal("tenant", tenantProbe.RootElement.GetProperty("decision").GetString());
        Assert.Equal(
            "byhost-a",
            tenantProbe.RootElement.GetProperty("tenant").GetProperty("name").GetString());

        // 受管域的根：定案为宿主，没有租户
        using var hostProbe = await ProbeAsync("example.com");
        Assert.Equal("host", hostProbe.RootElement.GetProperty("decision").GetString());
        Assert.True(
            hostProbe.RootElement.TryGetProperty("tenant", out var hostTenant) is false
            || hostTenant.ValueKind == JsonValueKind.Null);

        // 域外主机：域名不表态，后续解析来源仍可决定租户
        using var outsideProbe = await ProbeAsync("localhost");
        Assert.Equal("undecided", outsideProbe.RootElement.GetProperty("decision").GetString());
    }

    /// <summary>子域名指向一个不存在的租户时，请求在**租户解析阶段**就被拒，探测端点根本到不了。</summary>
    /// <remarks>
    /// 这条钉住的是"探测救不了配错的子域名"这件事：中间件按主机名解析出一个不存在的租户名，
    /// 整个请求（含匿名的探测端点）一律 404。登录页因此走探测失败那条分支，退回手填——
    /// 而在这个主机名上手填任何租户同样会被域名覆盖掉。
    /// 换句话说，这是部署配置问题，不是界面能兜住的状态；把它写成用例是为了让
    /// 有人日后把探测端点"改成能穿过解析失败"时，先看到这里的取舍。
    /// </remarks>
    [Fact]
    public async Task Subdomain_of_a_missing_tenant_is_rejected_during_resolution()
    {
        using var domainHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Leistd:MultiTenancy:DomainFormat"] = "{0}.example.com"
                })));

        using var client = ProjectWebApplicationFactory.CreateProjectClient(domainHost);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/by-host");
        request.Headers.Host = $"absent-{Guid.NewGuid():N}.example.com";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>匿名请求下，"租户不存在"与"租户已停用"必须给出同一种失败。</summary>
    /// <remarks>
    /// 两者不同就等于把"某个租户被停用了"这条业务情报告诉任何人。按名字查租户的匿名端点也因此被移除。
    /// </remarks>
    [Fact]
    public async Task An_anonymous_request_cannot_tell_an_unknown_tenant_from_an_inactive_one()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var inactiveId = await CreateTenantAsync(hostAdmin, "dormant");
        using (var deactivate = await hostAdmin.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{inactiveId}/activation", new { IsActive = false }))
        {
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }

        using var unknownClient = _factory.CreateProjectClient();
        unknownClient.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, Guid.NewGuid().ToString());
        using var inactiveClient = _factory.CreateProjectClient();
        inactiveClient.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, inactiveId.ToString());

        var unknown = await unknownClient.GetAsync("/api/v1/auth/security-config");
        var inactive = await inactiveClient.GetAsync("/api/v1/auth/security-config");

        Assert.Equal(unknown.StatusCode, inactive.StatusCode);
        // 逐字比响应体会把每次请求都不同的 traceId 也比进去；错误码才是契约
        Assert.Equal(await ErrorCodeAsync(unknown), await ErrorCodeAsync(inactive));
    }

    /// <summary>匿名请求**能**区分"租户存在"与"租户不存在"——这是有意接受的残留，不是缺陷。</summary>
    /// <remarks>
    /// <para>存在且启用的租户会走到业务逻辑（错误凭据 401），不存在的在中间件就被挡成 404。
    /// 一次请求即可判定，不需要爆破。</para>
    /// <para>把这条钉成用例，是因为它很容易被误当成 bug 去"修"：要抹平差别，得给不存在的租户
    /// 编造注册策略与验证码，登录页会照着虚构的策略渲染，比泄露存在性更糟。
    /// 真正敏感的启用状态与租户属性（id、展示名）匿名侧一概拿不到，由上一条用例保证。</para>
    /// <para>这条失败时不要改断言，先去看组件文档 multi-tenancy.md 的那一节还成不成立。</para>
    /// </remarks>
    [Fact]
    public async Task An_anonymous_request_can_still_tell_an_existing_tenant_from_an_unknown_one()
    {
        var hostAdmin = await LoginHostAdminAsync();
        await CreateTenantAsync(hostAdmin, "present");

        using var existingClient = _factory.CreateProjectClient();
        existingClient.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, "present");
        using var unknownClient = _factory.CreateProjectClient();
        unknownClient.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, "no-such-tenant");

        var existing = await existingClient.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "nobody@example.com", Password = "WrongPassword!1" });
        var unknown = await unknownClient.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "nobody@example.com", Password = "WrongPassword!1" });

        Assert.Equal(HttpStatusCode.Unauthorized, existing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>种子进行中的租户必须还没启用：否则它已经能被中间件接受，而此刻还没有管理员和权限授予。</summary>
    [Fact]
    public async Task Tenant_being_seeded_is_inactive_and_rejects_registration()
    {
        using var probingHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddTransient<TenantSeeder>();
                services.AddTransient<ITenantProvisioner, ProbingTenantSeeder>();
            }));

        ProbingTenantSeeder.Reset();
        ProbingTenantSeeder.Probe = tenantId => ProbeAnonymousRegistrationAsync(probingHost, tenantId);

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(probingHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var tenantId = await CreateTenantAsync(hostAdmin, "provisioning");

        // 探测发生在种子中途（角色、管理员都还没写完）：中间件必须拒绝这个尚未启用的租户，
        // 且与"租户不存在"给出同一种失败——匿名下不暴露租户处于哪种状态
        Assert.Equal(HttpStatusCode.NotFound, ProbingTenantSeeder.ProbedStatus);

        // 种子成功之后才激活：此刻租户正常可用，管理员能登录且只见自己的种子用户
        using var tenantClient = await LoginTenantAdminAsync(probingHost, tenantId);
        Assert.Equal(["admin"], await GetUsernamesAsync(tenantClient));
    }

    [Fact]
    public async Task Tenant_creation_phases_keep_their_own_boundaries_inside_an_outer_unit_of_work()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TenantCreationUnitOfWorkProbe>();
                services.AddTransient<EfCoreTenantManager<IdentityControlDbContext>>();
                services.AddTransient<ITenantManager, RecordingTenantManager>();
                services.AddTransient<TenantSeeder>();
                services.AddTransient<ITenantProvisioner, RecordingTenantSeeder>();
            }));

        using var scope = host.Services.CreateScope();
        var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        using var outerUnitOfWork = unitOfWorkManager.Begin(requiresNew: true);

        var name = $"outer-{Guid.NewGuid():N}";
        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantManagementService>().CreateAsync(
            new CreateTenantWithAdminInputDto
            {
                Name = name,
                DisplayName = $"{name} Inc.",
                AdminEmail = $"admin@{name}.example.com",
                AdminPassword = "Tenant@123456"
            });

        Assert.True(tenant.IsActive);
        Assert.Same(outerUnitOfWork, unitOfWorkManager.Current);

        var observations = scope.ServiceProvider
            .GetRequiredService<TenantCreationUnitOfWorkProbe>()
            .Observations;
        Assert.Equal(["control", "business", "activation"], observations.Select(item => item.Phase));
        Assert.All(observations, item => Assert.NotEqual(outerUnitOfWork.Id, item.UnitOfWorkId));
        Assert.Equal(3, observations.Select(item => item.UnitOfWorkId).Distinct().Count());
    }

    /// <summary>播种成功但激活失败：整个创建回滚，不留下"数据完整却永远停用"的租户。</summary>
    /// <remarks>
    /// 激活失败**必须**走补偿，不能因为"种子数据已完整"就放过：激活失败的现实成因之一
    /// 是另一个宿主管理员在播种期间删掉了这个租户，那时数据不是完整的而是孤儿的
    /// （落在一个软删租户 Id 下、永远不可达）。激活只有"租户不存在"和"数据库故障"两种失败，
    /// 两种情形下租户都不可用，因此统一补偿——比留一个需要人工判断的中间态干净。
    /// </remarks>
    [Fact]
    public async Task Failed_activation_rolls_back_creation_without_an_orphan_tenant()
    {
        using var brokenActivationHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddTransient<EfCoreTenantManager<IdentityControlDbContext>>();
                services.AddTransient<ITenantManager, FailActivationTenantManager>();
            }));

        FailActivationTenantManager.Reset();

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            brokenActivationHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var failed = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "halfway",
            AdminEmail = "admin@halfway.example.com",
            AdminPassword = "Tenant@123456"
        });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        var tenantId = FailActivationTenantManager.LastCreatedId;
        Assert.NotNull(tenantId);

        // 补偿覆盖了一次完整成功的播种：角色、授予、管理员全部清掉
        await AssertNoVisibleTenantDataAsync(brokenActivationHost, tenantId.Value);

        // 补偿后注册表里不该留下这个租户（按名字查的匿名端点已移除，直接查控制库更直接）
        await AssertTenantAbsentAsync(brokenActivationHost, "halfway");
    }

    /// <summary>清种子失败不能拖累删注册表：两步各用一个作用域。</summary>
    /// <remarks>
    /// 删注册表是"租户从此不可达"的最后一道保障。共用作用域时，清种子那一步的
    /// SaveChanges 失败会在跟踪器里留下实体，删注册表的 SaveChanges 会把它们一起写出去，
    /// 甚至因它们再次失败——于是租户既没清干净、又留在注册表里对外可用。
    /// </remarks>
    [Fact]
    public async Task Registry_is_removed_even_when_seed_cleanup_fails()
    {
        using var brokenPurgeHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddTransient<ITenantProvisioner, FailingPurgeTenantSeeder>()));

        FailingPurgeTenantSeeder.Reset();

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(
            brokenPurgeHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var failed = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "purge-broken",
            AdminEmail = "admin@purge-broken.example.com",
            AdminPassword = "Tenant@123456"
        });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(FailingPurgeTenantSeeder.PurgeAttempted, "Compensation never attempted to purge the seed data; the target path was not exercised");

        // 清种子抛错且留下脏跟踪器，注册表删除仍然成功：租户不可达
        // 补偿后注册表里不该留下这个租户（按名字查的匿名端点已移除，直接查控制库更直接）
        await AssertTenantAbsentAsync(brokenPurgeHost, "purge-broken");

        // 脏跟踪器里的实体没有被删注册表那一步顺手写进库
        using var scope = brokenPurgeHost.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.Empty(await db.Set<Role>()
            .IgnoreQueryFilters()
            .Where(r => r.Name == FailingPurgeTenantSeeder.GhostRoleName)
            .ToListAsync());
    }

    /// <summary>
    /// 部分播种后失败：角色已落库、用户尚未写入时补偿，必须连已写入的种子数据一起回滚。
    /// 只软删注册表是不够的——旧租户 Id 下会永久残留角色与授权版本。
    /// </summary>
    [Fact]
    public async Task Partial_seeding_failure_rolls_back_tenant_and_seed_data()
    {
        // 装饰真实种子：先让它写完角色与权限授予，再抛错——覆盖"部分落库"这条真实路径
        using var brokenHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                // 桩把 Purge 委托给真实实现，因此按具体类型注册它——
                // 若桩注入 ITenantProvisioner 会解析到自己，形成循环依赖
                services.AddTransient<TenantSeeder>();
                services.AddTransient<ITenantProvisioner, FailAfterRolesTenantSeeder>();
            }));

        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(brokenHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var failed = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "compensated",
            AdminEmail = "admin@compensated.example.com",
            AdminPassword = "Tenant@123456"
        });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        var failedTenantId = FailAfterRolesTenantSeeder.LastTenantId;
        Assert.NotNull(failedTenantId);

        // 播种在独立工作单元里执行，失败时它回滚，补偿再按租户清理一遍；两步之后租户下不得残留主体。
        using (var scope = brokenHost.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
            using (tenant.Change(failedTenantId.Value))
            {
                Assert.Empty(await db.Set<User>().ToListAsync());
                Assert.Empty(await db.Set<Role>().ToListAsync());
            }
        }

        // 独立补偿作用域不得提交失败现场仍在跟踪的实体。
        using (var scope = brokenHost.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            Assert.Empty(await db.Set<Role>()
                .IgnoreQueryFilters()
                .Where(r => r.Name == FailAfterRolesTenantSeeder.GhostRoleName)
                .ToListAsync());
        }

        // 补偿后注册表里不该留下这个租户（按名字查的匿名端点已移除，直接查控制库更直接）
        await AssertTenantAbsentAsync(brokenHost, "compensated");

        // 派生宿主与工厂共享数据库，用健康宿主验证同一注册表可直接重试。
        var hostAdmin2 = await LoginHostAdminAsync();
        var retried = await CreateTenantAsync(hostAdmin2, "compensated");
        Assert.NotEqual(Guid.Empty, retried);
    }

    /// <summary>断言指定租户上下文内看不到任何租户化数据。</summary>
    /// <remarks>
    /// 第一步的类型清单是完整性锁：新增租户化实体（或种子开始写入新实体）时，
    /// 清单断言先失败，提醒同步补偿逻辑与此处断言——避免补偿悄悄漏掉新数据。
    /// </remarks>
    private static async Task AssertNoVisibleTenantDataAsync(
        WebApplicationFactory<Program> host, Guid tenantId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        var multiTenantEntities = db.Model.GetEntityTypes()
            .Where(t => typeof(IMultiTenant).IsAssignableFrom(t.ClrType))
            .Select(t => t.ClrType.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                nameof(AuthorizationVersionRecord),
#if (ExternalLogin)
                nameof(ExternalLoginConnection),
#endif
#if (IncludeNotifications)
                nameof(NotificationRecord),
#endif
#if (IncludeOperationRecords)
                nameof(OperationRecord),
#endif
                nameof(PermissionGrantRecord),
                nameof(Role),
                nameof(SettingRecord),
                nameof(User),
                nameof(UserRole),
                nameof(UserSession)
            },
            multiTenantEntities);

        // UserRole 已实现 IMultiTenant，因此也在上面那份类型清单里了。这条显式查询**仍然保留**：
        // 清单只证明"它受过滤器管辖"，而这里证明"确实没有活着的孤儿关联行"。
        // 两者覆盖不同的失效——若 TenantId 因故没落上，租户上下文内的 Assert.Empty 会看不见它，
        // 只有跨过全部过滤器、按 tenantUserIds 直查才抓得住。
        // IgnoreQueryFilters 会同时摘掉软删与租户两个过滤器，所以租户条件要显式写
        var tenantUserIds = await db.Set<User>()
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId)
            .Select(u => u.Id)
            .ToListAsync();

        if (tenantUserIds.Count > 0)
        {
            var liveLinks = await db.Set<UserRole>()
                .IgnoreQueryFilters()
                .Where(ur => tenantUserIds.Contains(ur.UserId) && !ur.IsDeleted)
                .ToListAsync();
            Assert.Empty(liveLinks);
        }

        using (currentTenant.Change(tenantId))
        {
            Assert.Empty(await db.Set<User>().ToListAsync());
            Assert.Empty(await db.Set<Role>().ToListAsync());
            Assert.Empty(await db.Set<PermissionGrantRecord>().ToListAsync());
            Assert.Empty(await db.Set<AuthorizationVersionRecord>().ToListAsync());
            Assert.Empty(await db.Set<UserRole>().ToListAsync());
            // 创建流程里埋了"用户已创建"的操作记录：补偿若漏清它，留下的是一条带租户归属的孤儿审计行——
            // 上面那份类型清单只证明它受过滤器管辖，证明不了补偿真的清干净了。
#if (IncludeOperationRecords)
            Assert.Empty(await db.Set<OperationRecord>().ToListAsync());
#endif
            Assert.Empty(await db.Set<UserSession>().ToListAsync());
#if (ExternalLogin)
            Assert.Empty(await db.Set<ExternalLoginConnection>().ToListAsync());
#endif
#if (IncludeNotifications)
            Assert.Empty(await db.Set<NotificationRecord>().ToListAsync());
#endif
        }
    }

    /// <summary>播种期间的控制面竞争：另一个合法宿主管理员在种子还没跑完时动这个租户。</summary>
    /// <remarks>
    /// 用可阻塞种子把"播种中"这个瞬间拉长到可观测：种子先发出"我到了"信号，
    /// 然后挂住等测试放行。测试在这段时间里以第二个宿主会话发起竞争操作。
    /// 这是唯一能覆盖控制面并发的手法——真实播种是亚秒级的，靠时序碰不到。
    /// </remarks>
    [Fact]
    public async Task Concurrent_delete_during_seeding_leaves_no_orphan_data()
    {
        using var blockingHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddTransient<TenantSeeder>();
                services.AddTransient<ITenantProvisioner, BlockingTenantSeeder>();
            }));

        BlockingTenantSeeder.Reset();

        using var creator = await ProjectWebApplicationFactory.LoginAsync(blockingHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var racer = await ProjectWebApplicationFactory.LoginAsync(blockingHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var createTask = creator.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "raced-delete",
            AdminEmail = "admin@raced-delete.example.com",
            AdminPassword = "Tenant@123456"
        });

        // 等种子真的开始（角色已写入、管理员还没写），此刻注册表里已有一个停用租户
        var tenantId = await BlockingTenantSeeder.WaitUntilSeedingAsync();

        var delete = await racer.Client.DeleteAsync($"/api/v1/tenants/{tenantId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        BlockingTenantSeeder.Release();
        var created = await createTask;

        // 激活撞上"租户已不存在"：按普通业务失败回 404。
        // 宿主会话不该因为一个别人删掉的租户被登出——会话恢复只服务租户内会话
        Assert.Equal(HttpStatusCode.NotFound, created.StatusCode);

        // 关键断言：激活在补偿边界内，因此已写入的种子数据被一并清掉，
        // 不会永久残留在一个软删租户 Id 下
        await AssertNoVisibleTenantDataAsync(blockingHost, tenantId);
    }

    /// <summary>播种期间并发手动启用：租户里还没有用户，启用必须被拒绝。</summary>
    [Fact]
    public async Task Concurrent_activation_during_seeding_is_rejected()
    {
        using var blockingHost = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddTransient<TenantSeeder>();
                services.AddTransient<ITenantProvisioner, BlockingTenantSeeder>();
            }));

        BlockingTenantSeeder.Reset();

        using var creator = await ProjectWebApplicationFactory.LoginAsync(blockingHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var racer = await ProjectWebApplicationFactory.LoginAsync(blockingHost, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var createTask = creator.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "raced-activate",
            AdminEmail = "admin@raced-activate.example.com",
            AdminPassword = "Tenant@123456"
        });

        var tenantId = await BlockingTenantSeeder.WaitUntilSeedingAsync();

        // 抢先启用：租户内还没有任何用户，被业务规则挡住
        var activate = await racer.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/activation", new { IsActive = true });
        Assert.Equal(HttpStatusCode.Conflict, activate.StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            await ProbeAnonymousRegistrationAsync(blockingHost, tenantId));

        BlockingTenantSeeder.Release();
        var created = await createTask;
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var tenantClient = await LoginTenantAdminAsync(blockingHost, tenantId);
        Assert.Equal(["admin"], await GetUsernamesAsync(tenantClient));
    }

    /// <summary>真实种子跑到一半挂住，等测试放行；用来把"播种中"拉长成可观测的窗口。</summary>
    private sealed class BlockingTenantSeeder(
        ICurrentTenant currentTenant,
        TenantSeeder inner) : ITenantProvisioner
    {
        private static TaskCompletionSource<Guid> _seeding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static void Reset()
        {
            _seeding = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>等到种子真的开始执行，返回正在播种的租户 Id。</summary>
        internal static Task<Guid> WaitUntilSeedingAsync() => _seeding.Task;

        /// <summary>放行种子继续执行。</summary>
        internal static void Release() => _release.TrySetResult();

        public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
        {
            var tenantId = currentTenant.Id ?? throw new InvalidOperationException("Seeding must run inside a tenant context.");

            _seeding.TrySetResult(tenantId);
            await _release.Task;

            await inner.ProvisionAsync(context, cancellationToken);
        }

        public Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
            => inner.PurgeAsync(context, cancellationToken);
    }

    /// <summary>把远端地址改成公网测试地址（TEST-NET-3），使请求不落在任何受信任代理网段内。</summary>
    private sealed class UntrustedRemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
                    await continuation();
                });

                next(app);
            };
    }

    /// <summary>走完真实种子的角色与权限写入后抛错，制造"部分落库"的失败现场。</summary>
    private sealed class FailAfterRolesTenantSeeder(
        ICurrentTenant currentTenant,
        MyProjectDbContext dbContext,
        TenantSeeder inner) : ITenantProvisioner
    {
        /// <summary>失败瞬间留在跟踪器里、绝不应被补偿写入数据库的实体名。</summary>
        internal const string GhostRoleName = "GhostRoleFromDirtyTracker";

        /// <summary>最近一次失败的租户 Id，供测试断言其数据已被清除。</summary>
        internal static Guid? LastTenantId { get; private set; }

        public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
        {
            LastTenantId = currentTenant.Id;

            // 走完真实播种后在业务 UoW 提交前失败，同时覆盖
            // 角色、授权、管理员和关联数据的回滚/补偿。
            await inner.ProvisionAsync(context, cancellationToken);

            // 制造"脏跟踪器"：EF 在 SaveChanges 失败后会保留 Added/Modified 实体，
            // 这里用一个未保存的 Add 等价模拟——对补偿的影响完全相同。
            // 若补偿复用这个 DbContext，它的下一次 SaveChanges 会把这个实体一起写进库
            dbContext.Add(new Role(GhostRoleName, "Ghost Role"));

            throw new InvalidOperationException("injected seed failure before business unit-of-work commit");
        }

        public Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
            => inner.PurgeAsync(context, cancellationToken);
    }

    /// <summary>在真实种子开始之前探测租户此刻是否已对外可用，然后照常完成种子。</summary>
    private sealed class ProbingTenantSeeder(
        ICurrentTenant currentTenant,
        TenantSeeder inner) : ITenantProvisioner
    {
        /// <summary>由测试注入的探测动作（需要测试宿主的 HttpClient，桩自己造不出来）。</summary>
        internal static Func<Guid, Task<HttpStatusCode>>? Probe { get; set; }

        /// <summary>种子中途探测到的状态码。</summary>
        internal static HttpStatusCode? ProbedStatus { get; private set; }

        internal static void Reset()
        {
            Probe = null;
            ProbedStatus = null;
        }

        public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
        {
            var tenantId = currentTenant.Id ?? throw new InvalidOperationException("Seeding must run inside a tenant context.");

            if (Probe is not null)
            {
                // 探测请求走另一个 HttpClient，与本请求的租户上下文无关——它只带租户提示头
                ProbedStatus = await Probe(tenantId);
            }

            await inner.ProvisionAsync(context, cancellationToken);
        }

        public Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
            => inner.PurgeAsync(context, cancellationToken);
    }

    private sealed class TenantCreationUnitOfWorkProbe
    {
        private readonly List<(string Phase, Guid UnitOfWorkId)> _observations = [];

        public IReadOnlyList<(string Phase, Guid UnitOfWorkId)> Observations => _observations;

        public void Record(string phase, IUnitOfWorkManager unitOfWorkManager)
        {
            var unitOfWork = unitOfWorkManager.Current
                ?? throw new InvalidOperationException($"Tenant creation phase '{phase}' has no active unit of work.");
            _observations.Add((phase, unitOfWork.Id));
        }
    }

    private sealed class RecordingTenantSeeder(
        TenantSeeder inner,
        IUnitOfWorkManager unitOfWorkManager,
        TenantCreationUnitOfWorkProbe probe) : ITenantProvisioner
    {
        public Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
        {
            probe.Record("business", unitOfWorkManager);
            return inner.ProvisionAsync(context, cancellationToken);
        }

        public Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
            => inner.PurgeAsync(context, cancellationToken);
    }

    private sealed class RecordingTenantManager(
        EfCoreTenantManager<IdentityControlDbContext> inner,
        IUnitOfWorkManager unitOfWorkManager,
        TenantCreationUnitOfWorkProbe probe) : ITenantManager
    {
        public Task<TenantConfiguration> CreateAsync(
            string name,
            string? displayName,
            bool isActive,
            string? description = null,
            CancellationToken cancellationToken = default)
        {
            probe.Record("control", unitOfWorkManager);
            return inner.CreateAsync(name, displayName, isActive, description, cancellationToken);
        }

        public Task<TenantConfiguration> SetActiveAsync(
            Guid id,
            bool isActive,
            CancellationToken cancellationToken = default)
        {
            if (isActive)
            {
                probe.Record("activation", unitOfWorkManager);
            }

            return inner.SetActiveAsync(id, isActive, cancellationToken);
        }

        public Task<TenantConfiguration> UpdateAsync(
            Guid id,
            string name,
            string? displayName,
            string? description = null,
            CancellationToken cancellationToken = default)
            => inner.UpdateAsync(id, name, displayName, description, cancellationToken);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(id, cancellationToken);

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.FindAsync(id, cancellationToken);

        public Task<PagedResult<TenantConfiguration>> GetPagedAsync(
            string? keyword,
            bool? isActive,
            PageRequest page,
            CancellationToken cancellationToken = default)
            => inner.GetPagedAsync(keyword, isActive, page, cancellationToken);
    }

    /// <summary>种子失败、且清种子也失败并在自己的作用域里留下脏跟踪器。</summary>
    private sealed class FailingPurgeTenantSeeder(MyProjectDbContext dbContext) : ITenantProvisioner
    {
        /// <summary>清种子失败瞬间留在跟踪器里、绝不应被删注册表那一步写入的实体名。</summary>
        internal const string GhostRoleName = "GhostRoleFromFailedPurge";

        internal static bool PurgeAttempted { get; private set; }

        internal static void Reset() => PurgeAttempted = false;

        public Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("injected seed failure");

        public Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
        {
            PurgeAttempted = true;

            // 脏跟踪器 + 抛错：与"SaveChanges 失败后 EF 保留 Added 实体"等价。
            // 共用作用域时，删注册表的 SaveChanges 会把这一行一起写出去
            dbContext.Add(new Role(GhostRoleName, "Ghost Role"));
            throw new InvalidOperationException("injected purge failure");
        }
    }

    /// <summary>种子正常完成，但最后一步激活失败。</summary>
    private sealed class FailActivationTenantManager(EfCoreTenantManager<IdentityControlDbContext> inner) : ITenantManager
    {
        internal static Guid? LastCreatedId { get; private set; }

        internal static void Reset() => LastCreatedId = null;

        public async Task<TenantConfiguration> CreateAsync(
            string name,
            string? displayName,
            bool isActive,
            string? description = null,
            CancellationToken cancellationToken = default)
        {
            var created = await inner.CreateAsync(name, displayName, isActive, description, cancellationToken);
            LastCreatedId = created.Id;
            return created;
        }

        public Task<TenantConfiguration> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
            => isActive
                ? throw new InvalidOperationException("injected activation failure")
                : inner.SetActiveAsync(id, isActive, cancellationToken);

        public Task<TenantConfiguration> UpdateAsync(
            Guid id,
            string name,
            string? displayName,
            string? description = null,
            CancellationToken cancellationToken = default)
            => inner.UpdateAsync(id, name, displayName, description, cancellationToken);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(id, cancellationToken);

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.FindAsync(id, cancellationToken);

        public Task<PagedResult<TenantConfiguration>> GetPagedAsync(string? keyword, bool? isActive, PageRequest page, CancellationToken cancellationToken = default)
            => inner.GetPagedAsync(keyword, isActive, page, cancellationToken);
    }

#if (ExternalLogin)
    [Fact]
    public async Task Same_external_identity_links_per_tenant_and_queries_are_partitioned()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantAId = await CreateTenantAsync(hostAdmin, "extlogin-a");
        var tenantBId = await CreateTenantAsync(hostAdmin, "extlogin-b");

        // 外部身份的 (Provider, ProviderUserId) 由第三方决定，只在租户内唯一：
        // 同一个 GitHub 账号必须能在两个租户各自绑定，且各租户只看得见自己的连接
        const string provider = "github";
        const string providerUserId = "gh-42";

        var connectionAId = await BindExternalLoginAsync(tenantAId, provider, providerUserId);
        var connectionBId = await BindExternalLoginAsync(tenantBId, provider, providerUserId);
        Assert.NotEqual(connectionAId, connectionBId);

        Assert.Equal(connectionAId, await FindExternalLoginAsync(tenantAId, provider, providerUserId));
        Assert.Equal(connectionBId, await FindExternalLoginAsync(tenantBId, provider, providerUserId));

        Assert.Null(await FindExternalLoginAsync(tenantId: null, provider, providerUserId));
    }

    /// <summary>在指定租户上下文内为其管理员绑定一个外部身份，返回连接 Id。</summary>
    private async Task<Guid> BindExternalLoginAsync(Guid tenantId, string provider, string providerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
        var connections = scope.ServiceProvider.GetRequiredService<IRepository<ExternalLoginConnection, Guid>>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        using (currentTenant.Change(tenantId))
        {
            var admin = await users.GetFirstAsync(u => u.Username == "admin", q => q.OrderBy(u => u.Id));
            Assert.NotNull(admin);

            // TenantId 由多租户落值拦截器按当前上下文填充，业务代码不手写
            var connection = new ExternalLoginConnection(admin.Id, provider, providerUserId, new ExternalProfile(clock.Now, null, null, null));
            await connections.InsertAsync(connection);
            return connection.Id;
        }
    }

    /// <summary>在指定租户上下文（null 为宿主）内按外部身份查找连接，返回连接 Id 或 null。</summary>
    private async Task<Guid?> FindExternalLoginAsync(Guid? tenantId, string provider, string providerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var connections = scope.ServiceProvider.GetRequiredService<IRepository<ExternalLoginConnection, Guid>>();

        using (currentTenant.Change(tenantId))
        {
            var found = await connections.GetFirstAsync(
                c => c.Provider == provider && c.ProviderUserId == providerUserId,
                q => q.OrderBy(c => c.Id));
            return found?.Id;
        }
    }

#endif
    [Fact]
    public async Task Deleted_tenant_name_is_reusable_and_the_old_tenant_is_unreachable()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "recycled");

        var delete = await hostAdmin.Client.DeleteAsync($"/api/v1/tenants/{tenantId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var stale = _factory.CreateProjectClient();
        stale.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        var staleRequest = await stale.GetAsync("/api/v1/auth/security-config");
        Assert.Equal(HttpStatusCode.NotFound, staleRequest.StatusCode);

        var recreated = await CreateTenantAsync(hostAdmin, "recycled");
        Assert.NotEqual(tenantId, recreated);
    }

    /// <summary>删除幂等：不存在与重复删除都是 204，只有真正发生的那次删除留下成功记录，其他租户不受影响。</summary>
    [Fact]
    public async Task Deleting_a_missing_or_already_deleted_tenant_returns_204_without_operation_records()
    {
        var hostAdmin = await LoginHostAdminAsync();
        var tenantId = await CreateTenantAsync(hostAdmin, "idem-delete");
        var bystanderId = await CreateTenantAsync(hostAdmin, "idem-bystander");
        var missingId = Guid.NewGuid();

        var missing = await hostAdmin.Client.DeleteAsync($"/api/v1/tenants/{missingId}");
        var first = await hostAdmin.Client.DeleteAsync($"/api/v1/tenants/{tenantId}");
        var repeated = await hostAdmin.Client.DeleteAsync($"/api/v1/tenants/{tenantId}");

        Assert.Equal(
            (HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent),
            (missing.StatusCode, first.StatusCode, repeated.StatusCode));
        Assert.Equal(0, await OperationRecordQueries.CountSucceededAsync(
            _factory, hostAdmin.Client, OperationRecordActions.TenantDeleted, missingId.ToString()));
        Assert.Empty(await OperationRecordQueries.GetFailuresAsync(
            _factory, hostAdmin.Client, OperationRecordActions.TenantDeleted, missingId.ToString()));
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(
            _factory, hostAdmin.Client, OperationRecordActions.TenantDeleted, tenantId.ToString()));
        Assert.Empty(await OperationRecordQueries.GetFailuresAsync(
            _factory, hostAdmin.Client, OperationRecordActions.TenantDeleted, tenantId.ToString()));

        var bystander = await hostAdmin.Client.GetAsync($"/api/v1/tenants/{bystanderId}");
        Assert.Equal(HttpStatusCode.OK, bystander.StatusCode);
        using var body = JsonDocument.Parse(await bystander.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("isActive").GetBoolean());
    }

    /// <summary>租户列表固定按创建时间倒序、同刻按 Id 倒序：创建时间相同的租户逐页翻完不重不漏，关键字照常过滤。</summary>
    /// <remarks>
    /// 租户按 Id 递增的顺序写入，物理顺序与 Id 倒序相反；缺少 Id 尾序时同刻记录按存储顺序或任意顺序返回，断言即失败。
    /// 同刻且不匹配关键字的租户不能出现在任何一页里。
    /// </remarks>
    [Fact]
    public async Task Tenants_created_at_the_same_instant_page_by_id_descending_with_a_keyword()
    {
        var hostAdmin = await LoginHostAdminAsync();
        List<Guid> matching = [];
        foreach (var name in new[] { "tiepage-a", "tiepage-b", "tiepage-c", "tiepage-d" })
        {
            matching.Add(await CreateTenantAsync(hostAdmin, name));
        }

        var unrelated = await CreateTenantAsync(hostAdmin, "tieother");

        using (var scope = _factory.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<IdentityControlDbContext>();
            var tie = await control.Set<TenantRecord>().Where(record => record.Id == matching[0])
                .Select(record => record.CreationTime).SingleAsync();
            List<Guid> tied = [.. matching, unrelated];
            await control.Set<TenantRecord>().Where(record => tied.Contains(record.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.CreationTime, tie));
        }

        List<Guid> paged = [];
        for (var offset = 0; offset < matching.Count; offset++)
        {
            using var page = JsonDocument.Parse(await hostAdmin.Client.GetStringAsync(
                $"/api/v1/tenants?offset={offset}&limit=1&keyword=tiepage"));
            Assert.Equal(matching.Count, page.RootElement.GetProperty("totalCount").GetInt32());
            paged.Add(Assert.Single(page.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        }

        // PostgreSQL 的 uuid 按字节比较，与小写无连字符十六进制串的序数次序一致
        Assert.Equal(matching.OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal), paged);
        Assert.DoesNotContain(unrelated, paged);
    }
}
