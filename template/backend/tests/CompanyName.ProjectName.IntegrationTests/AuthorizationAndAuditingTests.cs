#if (LocalIdentity)
using Leistd.Authorization.Constants;
using Leistd.Authorization.Dtos;
using Leistd.Authorization.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.Initialization;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Roles.Dtos;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Application.Users.Dtos;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Authorization.Definitions;
using Leistd.Settings.Definitions;
using Leistd.Authorization.Errors;
using Leistd.Authorization.Grants;
using Leistd.Lock.Abstractions;

namespace CompanyName.ProjectName.IntegrationTests;

public sealed class AuthorizationAndAuditingTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Permissions_should_distinguish_users_roles_and_super_admin()
    {
        using var anonymous = Factory.CreateProjectClient();
        var anonymousResponse = await anonymous.GetAsync("/api/v1/users?offset=0&limit=10");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(superAdmin.Client)).StatusCode);

        var member = await CreateUserAsync(superAdmin.Client);
        using var memberSession = await Factory.LoginAsync(member.Username, TestPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetUsersAsync(memberSession.Client)).StatusCode);

        await GrantAsync(PermissionGrantProviderNames.User, member.Id, PermissionConstant.Users.Default);
        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(memberSession.Client)).StatusCode);

        var role = await CreateRoleAsync(superAdmin.Client);
        var roleUser = await CreateUserAsync(superAdmin.Client, [role.Id]);
        using var roleSession = await Factory.LoginAsync(roleUser.Username, TestPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetUsersAsync(roleSession.Client)).StatusCode);

        await GrantAsync(PermissionGrantProviderNames.Role, role.Id, PermissionConstant.Users.Default);
        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(roleSession.Client)).StatusCode);
    }

    [Fact]
    public async Task Admin_role_is_seeded_with_every_permission_and_has_no_code_level_bypass()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var adminRoleId = await GetRoleIdAsync(AdminConstant.RoleName);
        var adminRoleUser = await CreateUserAsync(superAdmin.Client, [adminRoleId]);
        using var adminSession = await Factory.LoginAsync(adminRoleUser.Username, TestPassword);

        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(adminSession.Client)).StatusCode);

        await ReplaceGrantsAsync(PermissionGrantProviderNames.Role, adminRoleId, []);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetUsersAsync(adminSession.Client)).StatusCode);

        // 恢复，避免影响同一 fixture 中的其他用例。
        await GrantAllAsync(adminRoleId);
    }


    [Fact]
    public async Task Update_permission_alone_cannot_change_roles()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var operatorUser = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(
            PermissionGrantProviderNames.User,
            operatorUser.Id,
            PermissionConstant.Users.Default,
            PermissionConstant.Users.Update,
            PermissionConstant.Users.Create);

        using var session = await Factory.LoginAsync(operatorUser.Username, TestPassword);
        var target = await CreateUserAsync(superAdmin.Client);
        var adminRoleId = await GetRoleIdAsync(AdminConstant.RoleName);

        // 普通更新不接受角色字段；角色分配端点需要 ManageRoles。
        var updateResponse = await session.Client.PutAsJsonAsync(
            $"/api/v1/users/{target.Id}",
            new UpdateUserInputDto
            {
                Email = target.Email,
                DisplayName = "renamed by operator"
            });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var assignResponse = await session.Client.PutAsJsonAsync(
            $"/api/v1/users/{target.Id}/roles",
            new UpdateUserRolesInputDto { RoleIds = [adminRoleId] });
        Assert.Equal(HttpStatusCode.Forbidden, assignResponse.StatusCode);

        // 创建时携带角色同样受 ManageRoles 保护，否则只需创建权限即可造出管理员。
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var createResponse = await session.Client.PostAsJsonAsync(
            "/api/v1/users",
            new CreateUserInputDto
            {
                Username = $"user_{suffix}",
                Email = $"user_{suffix}@example.test",
                Password = TestPassword,
                IsActive = true,
                RoleIds = [adminRoleId]
            });
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
    }

    [Fact]
    public async Task ManageRoles_permission_alone_cannot_update_the_profile()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var roleManager = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(
            PermissionGrantProviderNames.User,
            roleManager.Id,
            PermissionConstant.Users.ManageRoles);

        using var session = await Factory.LoginAsync(roleManager.Username, TestPassword);
        var target = await CreateUserAsync(superAdmin.Client);
        var role = await CreateRoleAsync(superAdmin.Client);

        var assignResponse = await session.Client.PutAsJsonAsync(
            $"/api/v1/users/{target.Id}/roles",
            new UpdateUserRolesInputDto { RoleIds = [role.Id] });
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);

        var updateResponse = await session.Client.PutAsJsonAsync(
            $"/api/v1/users/{target.Id}",
            new UpdateUserInputDto
            {
                Email = target.Email,
                DisplayName = "should not be allowed"
            });
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task Granting_a_child_permission_completes_its_parent()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);

        await ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            role.Id,
            [PermissionConstant.Users.Create]);

        var response = await superAdmin.Client.GetAsync($"/api/v1/permissions/grants/roles/{role.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var grants = await response.Content.ReadFromJsonAsync<PermissionGrantsResponse>();
        Assert.NotNull(grants);

        var parent = grants.Grants.Single(x => x.Name == PermissionConstant.Users.Default);
        Assert.True(parent.Granted);
    }


    [Fact]
    public async Task Disabling_a_user_revokes_their_existing_session()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(PermissionGrantProviderNames.User, user.Id, PermissionConstant.Users.Default);

        using var session = await Factory.LoginAsync(user.Username, TestPassword);
        Assert.Equal(
            HttpStatusCode.OK,
            (await session.Client.GetAsync("/api/v1/users?offset=0&limit=10")).StatusCode);

        var disabled = await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        // 登录时会拒绝禁用账号，但已签发的 Cookie 不会因此失效——放行就等于"禁用用户"
        // 只挡新登录，已在线的会话照常畅通。
        // 401 而非 403：账号失效说明这份凭据代表的身份已经不成立，属于"凭据无效"而非"权限不足"，
        // 前端也只把 401 当会话失效来清理登录态。
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await session.Client.GetAsync("/api/v1/users?offset=0&limit=10")).StatusCode);
    }

    [Fact]
    public async Task Undefined_permissions_surface_as_bad_request_not_server_error()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);

        // 未定义权限由授予管理器统一拒绝；应用层只做翻译，不重复判断。
        // 若这条异常没有被翻译，全局处理器会把它归一化成 500——这正是要锁住的行为。
        var response = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new
            {
                expectedVersion = 0,
                permissionNames = new[] { "App.NotDefined" }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Managing_permissions_implies_reading_the_permission_definitions()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var operatorUser = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(operatorUser.Username, TestPassword);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await session.Client.GetAsync("/api/v1/permissions/definitions")).StatusCode);

        // 只授予「配置角色权限」：权限树只为授予而读，不另设「查看权限目录」权限——
        // 要求额外记得授一个根权限只会制造「有权限却打不开界面」的无用状态。
        await GrantAsync(
            PermissionGrantProviderNames.User,
            operatorUser.Id,
            PermissionConstant.Roles.ManagePermissions);

        Assert.Equal(
            HttpStatusCode.OK,
            (await session.Client.GetAsync("/api/v1/permissions/definitions")).StatusCode);
    }


    /// <summary>
    /// 显示（语言与时区）组织与个人两级都有：个人覆盖谁都能写，组织默认要显示分组的子权限。
    /// </summary>
    [Fact]
    public async Task Display_defaults_need_the_display_permission_but_personal_overrides_do_not()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var timeZone = SettingConstant.Display.TimeZone;

        var member = await CreateUserAsync(superAdmin.Client);
        using (var session = await Factory.LoginAsync(member.Username, TestPassword))
        {
            var listed = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
            var setting = Assert.Single(listed.EnumerateArray(), s => s.GetProperty("name").GetString() == timeZone);
            Assert.False(setting.GetProperty("canManage").GetBoolean());
            Assert.True(setting.GetProperty("allowsTenantScope").GetBoolean());

            using (var own = await session.Client.PutAsJsonAsync("/api/v1/settings/current-user", new { Name = timeZone, Value = "Asia/Tokyo" }))
                Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
            using (var organization = await session.Client.PutAsJsonAsync("/api/v1/settings/current-tenant", new { Name = timeZone, Value = "Asia/Tokyo" }))
                Assert.Equal(HttpStatusCode.Forbidden, organization.StatusCode);
        }

        var displayAdmin = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(PermissionGrantProviderNames.User, displayAdmin.Id, PermissionConstant.Settings.Display);
        using var display = await Factory.LoginAsync(displayAdmin.Username, TestPassword);
        try
        {
            using var accepted = await display.Client.PutAsJsonAsync("/api/v1/settings/current-tenant", new { Name = timeZone, Value = "Europe/Paris" });
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }
        finally
        {
            using var cleared = await display.Client.PutAsJsonAsync("/api/v1/settings/current-tenant", new { Name = timeZone, Value = (string?)null });
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        }
    }

    /// <summary>
    /// 系统设置按分组授权：只有某个分组的子权限时，读不到也写不了别的分组；只有父权限时只能进页面，没有可管理的设置。
    /// </summary>
    [Fact]
    public async Task System_settings_are_managed_per_group()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var entryOnly = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(PermissionGrantProviderNames.User, entryOnly.Id, PermissionConstant.Settings.Default);
        using (var session = await Factory.LoginAsync(entryOnly.Username, TestPassword))
        {
            var listed = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
            Assert.DoesNotContain(listed.EnumerateArray(), setting => setting.GetProperty("canManage").GetBoolean());
            Assert.DoesNotContain(listed.EnumerateArray(), setting => setting.GetProperty("name").GetString() == SettingConstant.Logging.MinimumLevel);
        }

        // 授子权限会补齐父权限
        var operatorUser = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(PermissionGrantProviderNames.User, operatorUser.Id, PermissionConstant.Settings.Operations);
        using var operations = await Factory.LoginAsync(operatorUser.Username, TestPassword);

        var visible = (await operations.Client.GetFromJsonAsync<JsonElement>("/api/v1/settings"))
            .EnumerateArray()
            .Where(setting => setting.GetProperty("canManage").GetBoolean())
            .Select(setting => setting.GetProperty("group").GetString())
            .ToHashSet();
        Assert.Equal([SettingConstant.Groups.Operations], visible);

        using (var denied = await operations.Client.PutAsJsonAsync(
                   "/api/v1/settings/current-tenant",
                   new { Name = SettingConstant.Security.RequireTwoFactor, Value = "true" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // 操作记录的"凭什么"是该分组的子权限，不是入口权限
        var target = $"{SettingScopes.Host}/{SettingConstant.Logging.RequestLevel}";
        Task<int> RecordedAsync() => OperationRecordQueries.CountSucceededAsync(
            Factory, superAdmin.Client, OperationRecordActions.SettingChanged, target,
            authorizationBasis: PermissionConstant.Settings.Operations);
        var recordedBefore = await RecordedAsync();

        using (var accepted = await operations.Client.PutAsJsonAsync(
                   "/api/v1/settings/current-tenant",
                   new { Name = SettingConstant.Logging.RequestLevel, Value = "Warning" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        using var cleared = await operations.Client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant",
            new { Name = SettingConstant.Logging.RequestLevel, Value = (string?)null });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal(recordedBefore + 2, await RecordedAsync());
    }

    [Fact]
    public async Task Revoking_an_admin_permission_survives_re_initialization()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var adminRole = await GetRoleByNameAsync(AdminConstant.RoleName);
        var grants = await superAdmin.Client
            .GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{adminRole.Id}");
        Assert.NotNull(grants);

        var kept = grants.Grants
            .Where(x => x.Granted && x.Name != PermissionConstant.Users.Delete)
            .Select(x => x.Name)
            .ToArray();
        Assert.DoesNotContain(PermissionConstant.Users.Delete, kept);

        var revoke = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{adminRole.Id}",
            new { expectedVersion = grants.Version, permissionNames = kept });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // 再跑一次初始化：Admin 是普通角色，撤权就该一直是撤掉的状态。
        // 启动时"补齐缺失权限"看着无害，实际会把人工撤权原样加回来，"可撤权"就成了空话。
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISystemInitializer>().InitializeAsync();
        }

        var after = await superAdmin.Client
            .GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{adminRole.Id}");
        Assert.NotNull(after);
        Assert.False(after.Grants.Single(x => x.Name == PermissionConstant.Users.Delete).Granted);
        Assert.True(after.Grants.Single(x => x.Name == PermissionConstant.Users.Default).Granted);
    }

    [Fact]
    public async Task Admin_permission_seeding_recovers_when_a_previous_run_was_interrupted()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var adminRole = await GetRoleByNameAsync(AdminConstant.RoleName);

        // 复刻"角色已建、权限未播"：初始化没有事务，角色是立即落库的，这一状态确实可达。
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var providerKey = adminRole.Id.ToString();

            dbContext.RemoveRange(await dbContext.Set<PermissionGrantRecord>()
                .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey)
                .ToListAsync());
            dbContext.RemoveRange(await dbContext.Set<AuthorizationVersionRecord>()
                .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey)
                .ToListAsync());
            await dbContext.SaveChangesAsync();
        }

        var interrupted = await superAdmin.Client
            .GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{adminRole.Id}");
        Assert.NotNull(interrupted);
        Assert.Equal(0, interrupted.Version);
        Assert.DoesNotContain(interrupted.Grants, x => x.Granted);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISystemInitializer>().InitializeAsync();
        }

        // 判据是"授权版本为 0"而不是"本次新建了角色"：只认后者的话，那一次中断会让 Admin 永久缺权限。
        var recovered = await superAdmin.Client
            .GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{adminRole.Id}");
        Assert.NotNull(recovered);
        Assert.All(recovered.Grants, grant => Assert.True(grant.Granted));
    }

    [Fact]
    public async Task Concurrent_initializers_are_serialized_by_the_initialization_lock()
    {
        // 断言的是"初始化确实在锁内执行"，不是"并发时会崩"：测试宿主是单进程，
        // 多实例同时初始化的真实竞争在这里复现不出来，
        // 写成"不抛异常即通过"的用例无论有没有锁都会绿，等于没测。
        // 跨进程互斥由部署侧保证——多副本必须配置 Redis，那条路径不在集成测试范围内。
        //
        // 探针替换 IDistributedLock 而不是在初始化之后另取一把锁：后者两个任务本来就会被
        // 那把锁串行，与初始化有没有加锁无关，测不出任何东西。
        var probe = new LockUsageProbe();

        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDistributedLock>();
            services.AddSingleton<IDistributedLock>(probe);
        }));

        using var superAdmin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var adminRole = await GetRoleByNameAsync(AdminConstant.RoleName);

        // 复刻多实例同时启动：清掉授予与版本行，让两个 initializer 都看到"尚未播种"。
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var providerKey = adminRole.Id.ToString();

            dbContext.RemoveRange(await dbContext.Set<PermissionGrantRecord>()
                .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey)
                .ToListAsync());
            dbContext.RemoveRange(await dbContext.Set<AuthorizationVersionRecord>()
                .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey)
                .ToListAsync());
            await dbContext.SaveChangesAsync();
        }

        using var barrier = new Barrier(2);

        async Task RunInitializerAsync()
        {
            await using var scope = host.Services.CreateAsyncScope();
            var initializer = scope.ServiceProvider.GetRequiredService<ISystemInitializer>();
            barrier.SignalAndWait();
            await initializer.InitializeAsync();
        }

        // 宿主启动时已初始化一次，因此断言增量。
        var acquiredBefore = probe.AcquireCount;

        await Task.WhenAll(Task.Run(RunInitializerAsync), Task.Run(RunInitializerAsync));

        Assert.Equal(2, probe.AcquireCount - acquiredBefore);
        Assert.Equal(1, probe.MaxConcurrentHolders);

        var grants = await superAdmin.Client
            .GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{adminRole.Id}");
        Assert.NotNull(grants);
        Assert.All(grants.Grants, grant => Assert.True(grant.Granted));
        Assert.Equal(1, grants.Version);
    }

    /// <summary>记录初始化锁使用情况的替身：本身提供真实互斥，同时把"取过几次""同时几人持有"暴露出来。</summary>
    private sealed class LockUsageProbe : IDistributedLock
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private int acquireCount;
        private int currentHolders;
        private int maxConcurrentHolders;

        public int AcquireCount => Volatile.Read(ref acquireCount);

        public int MaxConcurrentHolders => Volatile.Read(ref maxConcurrentHolders);

        public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);

            if (key == SystemInitializer.InitializationLockKey)
            {
                Interlocked.Increment(ref acquireCount);
                var holders = Interlocked.Increment(ref currentHolders);
                InterlockedMax(ref maxConcurrentHolders, holders);
            }

            return new Handle(this, key);
        }

        public async Task<ILockHandle?> TryLockAsync(
            string key,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => await LockAsync(key, cancellationToken);

        private void Release(string key)
        {
            if (key == SystemInitializer.InitializationLockKey)
            {
                Interlocked.Decrement(ref currentHolders);
            }

            gate.Release();
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current) return;
                current = seen;
            }
        }

        private sealed class Handle(LockUsageProbe owner, string key) : ILockHandle
        {
            /// <summary>探针不模拟租约失效。</summary>
            public CancellationToken LockLost => CancellationToken.None;

            public ValueTask DisposeAsync()
            {
                owner.Release(key);
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Deleting_a_role_removes_its_grants_and_version()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);

        var seeded = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new { expectedVersion = 0, permissionNames = new[] { PermissionConstant.Users.Default } });
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await superAdmin.Client.DeleteAsync($"/api/v1/roles/{role.Id}")).StatusCode);

        // 授予与版本必须跟着角色一起消失：主体没了还留着版本行只会变成永久孤儿，
        // 而角色 Id 一旦被重用，新角色还会继承上一任的版本号。
        await using var scope = Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        var providerKey = role.Id.ToString();

        Assert.Empty(await dbContext.Set<PermissionGrantRecord>()
            .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey)
            .ToListAsync());
        Assert.Empty(await dbContext.Set<AuthorizationVersionRecord>()
            .Where(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey)
            .ToListAsync());
    }

    [Fact]
    public async Task Disabled_permissions_are_not_offered_by_the_definitions_endpoint()
    {
        // 有效启用是在定义加载时一次性预计算的，运行时改 IsEnabled 不生效，
        // 因此禁用必须发生在定义阶段——追加一个只在本宿主生效的定义提供器。
        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IPermissionDefinitionProvider, DisableUserDeleteProvider>()));

        using var superAdmin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var groups = await superAdmin.Client
            .GetFromJsonAsync<List<PermissionDefinitionGroupResponse>>("/api/v1/permissions/definitions");
        Assert.NotNull(groups);

        var names = groups.SelectMany(group => group.Permissions).SelectMany(Flatten).ToList();

        // 启用的父级下面挂着被禁用的子权限时，若照单下发，界面会渲染成可勾选项，
        // 而写入时又会以"未定义或已禁用"拒绝——用户只能拿到一个无从解释的 400。
        Assert.DoesNotContain(PermissionConstant.Users.Delete, names);
        Assert.Contains(PermissionConstant.Users.Default, names);
        Assert.Contains(PermissionConstant.Users.Create, names);

        static IEnumerable<string> Flatten(PermissionDefinitionResponse definition)
            => [definition.Name, .. definition.Children.SelectMany(Flatten)];
    }

    /// <summary>只在上面那条用例的宿主里禁用一个叶子权限，用于验证定义接口不下发禁用项。</summary>
    private sealed class DisableUserDeleteProvider : IPermissionDefinitionProvider
    {
        public void Define(IPermissionDefinitionContext context)
        {
            var permission = context.GetPermissionOrNull(PermissionConstant.Users.Delete);
            if (permission != null)
            {
                permission.IsEnabled = false;
            }
        }
    }

    [Fact]
    public async Task Concurrent_permission_saves_return_conflict_instead_of_overwriting()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);

        var first = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new
            {
                expectedVersion = 0,
                permissionNames = new[] { PermissionConstant.Users.Default }
            });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new
            {
                expectedVersion = 0,
                permissionNames = new[] { PermissionConstant.Roles.Default }
            });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = await superAdmin.Client
            .GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{role.Id}");
        Assert.NotNull(current);
        Assert.True(current.Grants.Single(x => x.Name == PermissionConstant.Users.Default).Granted);
        Assert.False(current.Grants.Single(x => x.Name == PermissionConstant.Roles.Default).Granted);
    }

    /// <summary>组件端点在授权之后被业务规则拒绝，也留下一条失败记录。</summary>
    /// <remarks>
    /// 权限管理端点由组件映射，拒绝发生在组件内部，由紧接授权之后、租户作用域之内的中间件补记。
    /// 目标标识与授权依据要与成功路径写下的逐字一致，按目标、按依据检索才查得全。
    /// </remarks>
    [Fact]
    public async Task A_rejected_permission_save_leaves_a_failure_record()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);

        await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new { expectedVersion = 0, permissionNames = new[] { PermissionConstant.Users.Default } });
        var stale = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new { expectedVersion = 0, permissionNames = new[] { PermissionConstant.Roles.Default } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var undefined = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new { expectedVersion = 1, permissionNames = new[] { "App.NotDefined" } });
        Assert.Equal(HttpStatusCode.BadRequest, undefined.StatusCode);

        var failures = await OperationRecordQueries.GetFailuresAsync(
            Factory, superAdmin.Client, OperationRecordActions.PermissionGrantsReplaced, $"Role/{role.Id}");
        Assert.Equal(
            [PermissionErrorCodes.UndefinedPermission, PermissionErrorCodes.ConcurrencyConflict],
            failures.Select(item => item.FailureCode));
        Assert.All(failures, item => Assert.Equal(PermissionConstant.Roles.ManagePermissions, item.AuthorizationBasis));
    }

    /// <summary>参数校验失败没有业务码，无从按原因聚合，不进操作记录。</summary>
    [Fact]
    public async Task A_permission_save_rejected_by_input_validation_leaves_no_record()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);

        var tooMany = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new
            {
                expectedVersion = 0,
                permissionNames = Enumerable.Range(0, ReplacePermissionGrantsInputDto.MaximumPermissionCount + 1)
                    .Select(index => $"App.Permission{index}")
                    .ToArray()
            });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        Assert.Empty(await OperationRecordQueries.GetFailuresAsync(
            Factory, superAdmin.Client, OperationRecordActions.PermissionGrantsReplaced, $"Role/{role.Id}"));
    }

    [Fact]
    public async Task Current_permissions_reflect_grants_and_change_version()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);

        var before = await session.Client.GetFromJsonAsync<CurrentPermissionsResponse>("/api/v1/permissions/current");
        Assert.NotNull(before);
        Assert.DoesNotContain(PermissionConstant.Users.Default, before.Permissions);
        Assert.False(before.IsSuperAdmin);

        await GrantAsync(PermissionGrantProviderNames.User, user.Id, PermissionConstant.Users.Default);

        var after = await session.Client.GetFromJsonAsync<CurrentPermissionsResponse>("/api/v1/permissions/current");
        Assert.NotNull(after);
        Assert.Contains(PermissionConstant.Users.Default, after.Permissions);
        Assert.NotEqual(before.VersionToken, after.VersionToken);
    }

    [Fact]
    public async Task Static_roles_cannot_be_deleted_and_assigned_roles_are_protected()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var adminRoleId = await GetRoleIdAsync(AdminConstant.RoleName);
        var staticDelete = await superAdmin.Client.DeleteAsync($"/api/v1/roles/{adminRoleId}");
        Assert.Equal(HttpStatusCode.Conflict, staticDelete.StatusCode);

        var role = await CreateRoleAsync(superAdmin.Client);
        await CreateUserAsync(superAdmin.Client, [role.Id]);

        var assignedDelete = await superAdmin.Client.DeleteAsync($"/api/v1/roles/{role.Id}");
        Assert.Equal(HttpStatusCode.Conflict, assignedDelete.StatusCode);
    }

    /// <summary>删除用户是软删除、关联行保留；只分配给已删除用户的角色不能因此永久删不掉，删除时连带清掉这些关联。</summary>
    [Fact]
    public async Task A_role_assigned_only_to_deleted_users_counts_no_users_and_can_be_deleted()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);
        var user = await CreateUserAsync(superAdmin.Client, [role.Id]);
        Assert.Equal(1, (await ReadRoleAsync(superAdmin.Client, role.Id)).GetProperty("userCount").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/users/{user.Id}")).StatusCode);
        Assert.Equal(0, (await ReadRoleAsync(superAdmin.Client, role.Id)).GetProperty("userCount").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/roles/{role.Id}")).StatusCode);
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.False(await db.Set<UserRole>().AnyAsync(userRole => userRole.RoleId == role.Id));
    }

    /// <summary>删角色、清关联、清授权与成功记录同在一个工作单元：最后一步失败时整体回滚，不留"接口报错但角色已删"的半成品。</summary>
    [Fact]
    public async Task A_failed_role_deletion_rolls_back_the_role_and_its_assignments()
    {
        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var inner = services.Last(descriptor => descriptor.ServiceType == typeof(IOperationRecorder));
            services.Remove(inner);
            services.Add(ServiceDescriptor.Describe(
                typeof(IOperationRecorder),
                provider => new RoleDeletionRecordFails((IOperationRecorder)(inner.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, inner.ImplementationType!))),
                inner.Lifetime));
        }));
        using var superAdmin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        // 带上授予：授予清理会在工作单元内先自行 SaveChanges，回滚必须连它一起撤回
        var role = await CreateRoleAsync(superAdmin.Client);
        var seeded = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new { expectedVersion = 0, permissionNames = new[] { PermissionConstant.Users.Default } });
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        var user = await CreateUserAsync(superAdmin.Client, [role.Id]);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/users/{user.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.InternalServerError, (await superAdmin.Client.DeleteAsync($"/api/v1/roles/{role.Id}")).StatusCode);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        Assert.True(await db.Roles.AnyAsync(existing => existing.Id == role.Id));
        Assert.True(await db.Set<UserRole>().AnyAsync(userRole => userRole.RoleId == role.Id));
        var providerKey = role.Id.ToString();
        Assert.True(await db.Set<PermissionGrantRecord>()
            .AnyAsync(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey));
        Assert.True(await db.Set<AuthorizationVersionRecord>()
            .AnyAsync(x => x.ProviderName == PermissionGrantProviderNames.Role && x.ProviderKey == providerKey));
    }

    private sealed class RoleDeletionRecordFails(IOperationRecorder inner) : IOperationRecorder
    {
        public Task RecordSucceededAsync(
            string action, OperationTarget target, string authorizationBasis, CancellationToken cancellationToken = default) =>
            action == OperationRecordActions.RoleDeleted
                ? throw new InvalidOperationException("Injected failure after the role was deleted.")
                : inner.RecordSucceededAsync(action, target, authorizationBasis, cancellationToken);

        public Task RecordFailedAsync(
            string action, OperationTarget target, string authorizationBasis, OperationFailure failure = default) =>
            inner.RecordFailedAsync(action, target, authorizationBasis, failure);
    }

    /// <summary>
    /// 管理员启停账号与重置密码改变的是"这个人能不能进来"，与其他账号管理操作一样留痕；
    /// 状态没变的重复操作不留记录。
    /// </summary>
    [Fact]
    public async Task Enabling_disabling_and_resetting_a_password_leave_operation_records()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await CreateUserAsync(superAdmin.Client);
        var targetId = user.Id.ToString();

        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.PostAsJsonAsync(
            $"/api/v1/users/{user.Id}/reset-password", new { Password = "IntegrationTests!Reset1" })).StatusCode);

        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserDisabled, targetId));
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserEnabled, targetId));
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserPasswordReset, targetId));
    }

    /// <summary>删除不存在的用户即成功：重复删除与删除从未存在的 Id 都返回 200，且不新增删除记录。</summary>
    [Fact]
    public async Task Deleting_a_missing_user_succeeds_without_a_record()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var user = await CreateUserAsync(superAdmin.Client);
        var neverExisted = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/users/{user.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/users/{user.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/users/{neverExisted}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await superAdmin.Client.GetAsync($"/api/v1/users/{user.Id}")).StatusCode);
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(
            Factory, superAdmin.Client, OperationRecordActions.UserDeleted, user.Id.ToString()));
        Assert.Equal(0, await OperationRecordQueries.CountSucceededAsync(
            Factory, superAdmin.Client, OperationRecordActions.UserDeleted, neverExisted.ToString()));
    }

    /// <summary>角色资料更新留成功记录；无更新权限被拒时由端点注解补一条失败记录，角色不变。</summary>
    [Fact]
    public async Task Updating_a_role_leaves_an_operation_record()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);
        var update = new { DisplayName = "Renamed role", Description = "Updated", Sort = 5, IsDefault = false };

        var updated = await superAdmin.Client.PutAsJsonAsync($"/api/v1/roles/{role.Id}", update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(
            Factory, superAdmin.Client, OperationRecordActions.RoleUpdated, role.Id.ToString()));

        var reader = await CreateUserAsync(superAdmin.Client);
        await GrantAsync(PermissionGrantProviderNames.User, reader.Id, PermissionConstant.Roles.Default);
        using var readerSession = await Factory.LoginAsync(reader.Username, TestPassword);
        var rejected = await readerSession.Client.PutAsJsonAsync(
            $"/api/v1/roles/{role.Id}", update with { DisplayName = "Hijacked" });

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("Renamed role", (await ReadRoleAsync(superAdmin.Client, role.Id)).GetProperty("displayName").GetString());
        var failures = await OperationRecordQueries.GetFailuresAsync(
            Factory, superAdmin.Client, OperationRecordActions.RoleUpdated, role.Id.ToString());
        Assert.Contains(failures, failure => failure.AuthorizationBasis == PermissionConstant.Roles.Update);
    }

#if (OpenIddictServer)
    /// <summary>开放应用的四个写操作各留一条成功记录；删除幂等，重复删除返回 200 且不新增记录。</summary>
    [Fact]
    public async Task Open_application_writes_leave_records_and_deletion_is_idempotent()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var clientId = $"audit-{Guid.NewGuid():N}"[..20];
        var body = new
        {
            ClientId = clientId,
            DisplayName = "Audited client",
            ApplicationType = "service",
            ClientType = "confidential",
            Permissions = new[] { "ept:token", "gt:client_credentials" },
            SessionBound = false
        };

        var created = await superAdmin.Client.PostAsJsonAsync("/api/v1/open-applications", body);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK,
            (await superAdmin.Client.PutAsJsonAsync($"/api/v1/open-applications/{id}", body with { DisplayName = "Renamed client" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await superAdmin.Client.PostAsync($"/api/v1/open-applications/{id}/reset-secret", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/open-applications/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.DeleteAsync($"/api/v1/open-applications/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await superAdmin.Client.GetAsync($"/api/v1/open-applications/{id}")).StatusCode);
        foreach (var action in new[]
                 {
                     OperationRecordActions.OpenApplicationCreated,
                     OperationRecordActions.OpenApplicationUpdated,
                     OperationRecordActions.OpenApplicationSecretReset,
                     OperationRecordActions.OpenApplicationDeleted
                 })
        {
            Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, action, id));
        }
    }

    /// <summary>取值范围与回调地址格式是入参校验：400 且字段错误落在对应字段，不带业务码，也不会建出客户端。</summary>
    [Theory]
    [InlineData("clientId", "   ", "web", "confidential", "https://localhost/cb")]
    [InlineData("applicationType", "bad-type-client", "desktop", "confidential", "https://localhost/cb")]
    [InlineData("clientType", "bad-kind-client", "web", "hybrid", "https://localhost/cb")]
    [InlineData("redirectUris", "bad-uri-client", "web", "confidential", "https://localhost/cb#fragment")]
    public async Task Invalid_open_application_input_is_a_field_error(
        string field, string clientId, string applicationType, string clientType, string redirectUri)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var response = await superAdmin.Client.PostAsJsonAsync("/api/v1/open-applications", new
        {
            ClientId = clientId,
            ApplicationType = applicationType,
            ClientType = clientType,
            RedirectUris = new[] { redirectUri },
            Permissions = new[] { "ept:authorization", "ept:token", "gt:authorization_code" },
            Requirements = new[] { "ft:pkce" },
            SessionBound = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(problem.RootElement.TryGetProperty("code", out _));
        Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == field);
        var page = await superAdmin.Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/open-applications?offset=0&limit=10&keyword={clientId.Trim()}");
        Assert.True(clientId.Trim().Length == 0 || page.GetProperty("totalCount").GetInt32() == 0);
    }
#endif

    /// <summary>请求体漏掉必填键：错误按字段落回输入框，而不是反序列化失败的 <c>$</c>；也不会建出角色。</summary>
    /// <remarks>入参 DTO 的必填写 <c>[Required]</c> 而不是 C# <c>required</c>，后者让缺键在模型验证之前就失败。</remarks>
    [Fact]
    public async Task Missing_required_keys_are_reported_per_field()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var before = await superAdmin.Client.GetFromJsonAsync<JsonElement>("/api/v1/roles?offset=0&limit=1");

        var response = await superAdmin.Client.PostAsJsonAsync("/api/v1/roles", new { sort = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["displayName", "name"],
            problem.RootElement.GetProperty("errors").EnumerateArray()
                .Select(error => error.GetProperty("field").GetString()).Order(StringComparer.Ordinal));
        var after = await superAdmin.Client.GetFromJsonAsync<JsonElement>("/api/v1/roles?offset=0&limit=1");
        Assert.Equal(before.GetProperty("totalCount").GetInt32(), after.GetProperty("totalCount").GetInt32());
    }

    private static async Task<JsonElement> ReadRoleAsync(HttpClient client, Guid roleId)
    {
        using var body = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/roles/{roleId}"));
        return body.RootElement.Clone();
    }

    /// <summary>控制器端点的业务拒绝走同一个中间件，与组件的 Minimal API 端点一致。</summary>
    /// <remarks>删除内置角色被规则拒绝：目标取路由上的 id，依据取控制器动作上的删除策略。</remarks>
    [Fact]
    public async Task A_rejected_role_deletion_leaves_a_failure_record()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var adminRoleId = await GetRoleIdAsync(AdminConstant.RoleName);

        var rejected = await superAdmin.Client.DeleteAsync($"/api/v1/roles/{adminRoleId}");
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        var failures = await OperationRecordQueries.GetFailuresAsync(
            Factory, superAdmin.Client, OperationRecordActions.RoleDeleted, adminRoleId.ToString());
        Assert.Contains((RoleErrorCodes.StaticRoleCannotBeDeleted, PermissionConstant.Roles.Delete), failures);
    }

    [Fact]
    public async Task Undefined_permissions_are_rejected_instead_of_being_stored()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = await CreateRoleAsync(superAdmin.Client);

        var response = await superAdmin.Client.PutAsJsonAsync(
            $"/api/v1/permissions/grants/roles/{role.Id}",
            new
            {
                expectedVersion = 0,
                permissionNames = new[] { "App.NotDefined" }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData("text/html,application/xhtml+xml")]
    [InlineData("text/html;q=0, application/json")]
    public async Task Unauthenticated_api_calls_answer_401_regardless_of_accept(string? accept)
    {
        using var anonymous = Factory.CreateProjectClient();
        if (accept != null)
        {
            anonymous.DefaultRequestHeaders.Add("Accept", accept);
        }

        // Cookie 中间件默认把一切未认证请求 302 到登录页/拒绝页；本宿主是 SPA + API，
        // 没有受保护的 SSR 页面需要那条分支，跟随重定向的客户端只会撞上 SPA 兜底拿到 HTML 200，
        // 把"未认证"伪装成成功。也不按 Accept 猜测：text/html;q=0 明确表示不接受 HTML，
        // 任何子串匹配都会把它误判成浏览器导航。
        var response = await anonymous.GetAsync("/api/v1/users?offset=0&limit=10");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Logging_out_works_even_after_the_account_was_disabled()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);

        Assert.Equal(
            HttpStatusCode.OK,
            (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null)).StatusCode);

        // 登出是幂等的 Cookie 清理。挂 [Authorize] 时账号一被禁用本人就清不掉服务端 Cookie——
        // 登不出去，浏览器里还留着一张已经没用的票。
        Assert.Equal(
            HttpStatusCode.OK,
            (await session.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_also_revokes_endpoints_that_only_require_authentication()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null)).StatusCode);

        // 只在权限判定里补检查的话，撤权只覆盖 RBAC 接口，`/auth/me` 这类"仅要求已认证"的端点照常畅通。
        // 这条要求挂在默认策略上，正是为了让这里也失效。
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

#if (IncludeNotifications || IncludeRealTime)
    [Fact]
    public async Task Disabling_a_user_blocks_new_hub_connections()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);

        // 打 negotiate 而不是引 SignalR.Client：Hub 端点的授权就发生在这一步，
        // 走的是同一条 RequireAuthorization() → 默认策略的路径，不必为一条测试加包依赖。
        const string Negotiate = ProjectWebApplicationFactory.HubPath + "/negotiate?negotiateVersion=1";
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsync(Negotiate, null)).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await superAdmin.Client.PatchAsync($"/api/v1/users/{user.Id}/disable", null)).StatusCode);

        // 注意这条测试锁住的是"新连接建不起来"。SignalR 只在握手阶段授权，
        // 已经建立的连接不会因为账号被禁用而断开，需要它也失效的项目得自己做连接注册表加跨节点终止通道。
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await session.Client.PostAsync(Negotiate, null)).StatusCode);
    }

#endif
    [Fact]
    public async Task Deleting_a_user_revokes_their_existing_session()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.True((await superAdmin.Client.DeleteAsync($"/api/v1/users/{user.Id}")).IsSuccessStatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>管理员不能停用或删除自己：拒绝发生在任何写入之前，本人的会话照常可用，也不留成功记录。</summary>
    /// <remarks>停用会当场作废本人全部会话；若拒绝落在撤销之后，请求虽然报错，操作者照样被关在门外。</remarks>
    [Fact]
    public async Task An_administrator_cannot_disable_or_delete_their_own_account()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        // 用自建角色显式授予所需权限：同类里有用例改动内置 Admin 角色的授予，依赖它会随执行顺序变化
        var role = await CreateRoleAsync(superAdmin.Client);
        await ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            role.Id,
            [PermissionConstant.Users.Default, PermissionConstant.Users.Update, PermissionConstant.Users.Delete]);
        var administrator = await CreateUserAsync(superAdmin.Client, [role.Id]);
        using var acting = await Factory.LoginAsync(administrator.Username, TestPassword);
        // 同一个人的另一台设备：撤销会话的话它最先失效
        using var otherDevice = await Factory.LoginAsync(administrator.Username, TestPassword);
        var targetId = administrator.Id.ToString();

        using (var disable = await acting.Client.PatchAsync($"/api/v1/users/{administrator.Id}/disable", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);
            Assert.Equal(UserErrorCodes.SelfDisableForbidden, await ErrorCodeAsync(disable));
        }

        using (var delete = await acting.Client.DeleteAsync($"/api/v1/users/{administrator.Id}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
            Assert.Equal(UserErrorCodes.SelfDeleteForbidden, await ErrorCodeAsync(delete));
        }

        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(acting.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetUsersAsync(otherDevice.Client)).StatusCode);
        var current = await superAdmin.Client.GetFromJsonAsync<UserManagementOutputDto>($"/api/v1/users/{administrator.Id}");
        Assert.True(current!.IsActive);
        Assert.Equal(0, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserDisabled, targetId));
        Assert.Equal(0, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserDeleted, targetId));

        // 规则只针对本人：另一位管理员照常可以停用他，他的会话随之失效
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.Client.PatchAsync($"/api/v1/users/{administrator.Id}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetUsersAsync(otherDevice.Client)).StatusCode);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>绕过应用直接改库锁定（无截止时间）的账号，已有会话在下一次会话校验时被拒。</summary>
    /// <remarks>
    /// 经应用停用、删除账号会当场撤销会话；直接改库不经过那条路，靠会话校验在缓存未命中时确认账号仍可用。
    /// 登录后先改库、再发第一个带 Cookie 的请求，校验必然落在缓存之外。
    /// </remarks>
    [Fact]
    public async Task Out_of_band_permanent_lock_rejects_the_session_at_its_next_check()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var user = await CreateUserAsync(superAdmin.Client);
        using var session = await Factory.LoginAsync(user.Username, TestPassword);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var entity = await dbContext.Set<User>().SingleAsync(x => x.Id == user.Id);
            entity.Lock();
            await dbContext.SaveChangesAsync();
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Audit_fields_should_follow_the_authenticated_operator()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var created = await CreateUserAsync(admin.Client);

        Guid adminId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            adminId = await db.Users.Where(user => user.IsSuperAdmin).Select(user => user.Id).SingleAsync();
            var entity = await db.Users.AsNoTracking().SingleAsync(user => user.Id == created.Id);
            Assert.Equal(adminId.ToString(), entity.CreatorId);
            Assert.NotEqual(default, entity.CreationTime);
        }

        var updateResponse = await admin.Client.PutAsJsonAsync(
            $"/api/v1/users/{created.Id}",
            new UpdateUserInputDto
            {
                Email = created.Email,
                DisplayName = "Audited user updated"
            });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var entity = await db.Users.AsNoTracking().SingleAsync(user => user.Id == created.Id);
            Assert.Equal(adminId.ToString(), entity.LastModifierId);
            Assert.NotNull(entity.LastModificationTime);
        }

        var deleteResponse = await admin.Client.DeleteAsync($"/api/v1/users/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var entity = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(user => user.Id == created.Id);
            Assert.True(entity.IsDeleted);
            Assert.Equal(adminId.ToString(), entity.DeleterId);
            Assert.NotNull(entity.DeletionTime);
        }
    }

    private static Task<HttpResponseMessage> GetUsersAsync(HttpClient client)
        => client.GetAsync("/api/v1/users?offset=0&limit=10");

    private async Task<Guid> GetRoleIdAsync(string roleName)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return await db.Roles.Where(role => role.Name == roleName).Select(role => role.Id).SingleAsync();
    }

    private async Task ReplaceGrantsAsync(
        string providerName,
        Guid providerKey,
        IReadOnlyCollection<string> permissionNames)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>();
        await manager.ReplaceGrantsAsync(providerName, providerKey.ToString(), permissionNames);
    }

    private async Task GrantAllAsync(Guid roleId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IPermissionDefinitionManager>();
        var manager = scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>();

        await manager.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            roleId.ToString(),
            [.. definitions.GetAll().Select(x => x.Name)]);
    }

    private static async Task<RoleOutputDto> CreateRoleAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var response = await client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleInputDto
            {
                Name = $"role_{suffix}",
                DisplayName = $"Integration role {suffix}"
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Create role failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RoleOutputDto>())!;
    }

    private async Task<Role> GetRoleByNameAsync(string name)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return await dbContext.Set<Role>().AsNoTracking().SingleAsync(role => role.Name == name);
    }

    private sealed record CurrentPermissionsResponse(string[] Permissions, bool IsSuperAdmin, string VersionToken);

    private sealed record PermissionDefinitionGroupResponse(
        string Name,
        string DisplayName,
        PermissionDefinitionResponse[] Permissions);

    private sealed record PermissionDefinitionResponse(
        string Name,
        string DisplayName,
        string? ParentName,
        PermissionDefinitionResponse[] Children);

    private sealed record PermissionGrantsResponse(long Version, PermissionGrantStateResponse[] Grants);

    private sealed record PermissionGrantStateResponse(string Name, bool Granted);

}
#endif
