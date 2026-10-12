#if (RemoteTokenAuth)
extern alias Migrator;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CompanyName.ProjectName.Application;
using CompanyName.ProjectName.Application.Initialization;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Authorization.Definitions;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Tenancy;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
using DatabaseMigrationRunner = Migrator::CompanyName.ProjectName.DbMigrator.Runners.DatabaseMigrationRunner;
using MigratorDependencyInjection = Migrator::CompanyName.ProjectName.DbMigrator.DependencyInjection;
using ResourceAdminBootstrapRunner = Migrator::CompanyName.ProjectName.DbMigrator.Runners.ResourceAdminBootstrapRunner;
using Leistd.DependencyInjection.DynamicProxy.Registration;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>正式管理员引导入口：真实 PostgreSQL、真实 CLI；授权载体是 Admin 角色，撤销后重跑不恢复，只有显式恢复模式才把人请回来。</summary>
public sealed class ResourceAdminBootstrapTests
{
    private const string Schema = "\"companyname-projectname\"";
    private const string Issuer = "https://identity.test/";

    [Fact]
    public async Task Dry_run_is_read_only_and_apply_assigns_the_admin_role_through_a_minimal_subject()
    {
        var connection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var subject = Guid.NewGuid();
            var before = await StateAsync(connection);
            var dry = await RunAsync(connection, "--grant-admin", subject.ToString());
            Assert.True(dry.Exit == 0, dry.Output);
            Assert.Contains("Dry run", dry.Output);
            Assert.Contains("create a minimal local user", dry.Output);
            Assert.Equal(before, await StateAsync(connection));

            var applied = await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply");
            Assert.True(applied.Exit == 0, applied.Output);
            Assert.Contains("Assigned the Admin role", applied.Output);

            // 投影前引导：只含主体标识的最小用户行，不编造资料
            Assert.Equal((subject.ToString(), string.Empty, true), await UserAsync(connection, subject));
            Assert.Equal(1, await AdminMembershipsAsync(connection, subject, deleted: false));
            // 权限经角色取得：不给这个人写直接授予，Admin 角色首次播种了全部权限
            Assert.Equal(0, await UserGrantCountAsync(connection, subject));
            Assert.True(await AdminRoleGrantCountAsync(connection) > 0);
#if (IncludeOperationRecords)
            Assert.Equal(before.Records + 1, (await StateAsync(connection)).Records);
#else
            Assert.Contains("resource.admin-granted", applied.Output);
            Assert.Contains("deployment:", applied.Output);
            Assert.Contains("DeploymentBootstrap", applied.Output);
#endif

            var settled = await StateAsync(connection);
            var again = await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply");
            Assert.True(again.Exit == 0, again.Output);
            Assert.Contains("already holds the Admin role", again.Output);
            // 无操作就是什么都不写：成员、授予、版本与操作记录一概不变
            Assert.Equal(settled, await StateAsync(connection));
        }
        finally
        {
            PostgreSqlTestDatabase.DropDatabase(connection);
        }
    }

    [Fact]
    public async Task Removing_the_membership_is_not_undone_by_re_running_the_command()
    {
        var connection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var subject = Guid.NewGuid();
            Assert.Equal(0, (await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply")).Exit);

            // 之后由角色管理把此人移出 Admin：成员关系软删除，留下历史
            await ExecuteAsync(connection,
                $"UPDATE {Schema}.\"UserRoles\" SET \"IsDeleted\" = true, \"DeletionTime\" = now() WHERE \"UserId\" = @subject",
                subject);

            var removed = await StateAsync(connection);
            var repeated = await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply");
            Assert.True(repeated.Exit == 0, repeated.Output);
            Assert.Contains("was removed from the Admin role earlier", repeated.Output);
            Assert.Equal(0, await AdminMembershipsAsync(connection, subject, deleted: false));
            Assert.Equal(1, await AdminMembershipsAsync(connection, subject, deleted: true));
            Assert.Equal(removed, await StateAsync(connection));
        }
        finally
        {
            PostgreSqlTestDatabase.DropDatabase(connection);
        }
    }

    [Fact]
    public async Task An_existing_user_is_reused_without_touching_its_profile_or_status()
    {
        var connection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var subject = Guid.NewGuid();
            // 已经访问过、被本服务停用的远端用户
            await ExecuteAsync(connection,
                $"INSERT INTO {Schema}.\"Users\" (\"Id\", \"Username\", \"Email\", \"DisplayName\", \"IsActive\", \"IsSuperAdmin\", \"CreationTime\", \"IsDeleted\") "
                + "VALUES (@subject, 'alice', 'alice@example.test', 'Alice', false, false, now(), false)",
                subject);

            var dry = await RunAsync(connection, "--grant-admin", subject.ToString());
            Assert.True(dry.Exit == 0, dry.Output);
            Assert.Contains("reuse local user 'alice' (inactive)", dry.Output);

            var applied = await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply");
            Assert.True(applied.Exit == 0, applied.Output);
            Assert.Equal(("alice", "alice@example.test", false), await UserAsync(connection, subject));
            Assert.Equal(1, await AdminMembershipsAsync(connection, subject, deleted: false));
        }
        finally
        {
            PostgreSqlTestDatabase.DropDatabase(connection);
        }
    }

    [Fact]
    public async Task A_user_deleted_in_this_service_is_not_restored()
    {
        var connection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var subject = Guid.NewGuid();
            await ExecuteAsync(connection,
                $"INSERT INTO {Schema}.\"Users\" (\"Id\", \"Username\", \"Email\", \"IsActive\", \"IsSuperAdmin\", \"CreationTime\", \"IsDeleted\", \"DeletionTime\") "
                + "VALUES (@subject, 'bob', '', true, false, now(), true, now())",
                subject);
            var before = await StateAsync(connection);

            var applied = await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply");
            Assert.NotEqual(0, applied.Exit);
            Assert.Contains("has been deleted", applied.Output);
            Assert.Equal(before, await StateAsync(connection));
        }
        finally
        {
            PostgreSqlTestDatabase.DropDatabase(connection);
        }
    }
    [Fact]
    public async Task A_subject_bootstrapped_before_its_first_request_is_projected_and_judged_by_the_admin_role()
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "bootstrap-test" };
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", "resource-api")
            .ConfigureTestServices(services => services.Configure<OpenIddictValidationOptions>(options =>
            {
                options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer) };
                options.Configuration.SigningKeys.Add(key);
            })));
        // 宿主启动时已按正式入口初始化并播种 Admin 角色
        var connection = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")!;

        // 投影前引导：这个人还从没访问过本服务
        var first = Guid.NewGuid();
        var applied = await RunAsync(connection, "--grant-admin", first.ToString(), "--apply");
        Assert.True(applied.Exit == 0, applied.Output);
        Assert.Contains("created a minimal local user", applied.Output);

        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(first, key, "carol", "carol@example.test"));
        using (var roles = await client.GetAsync("/api/v1/roles"))
        {
            Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        }

        // 首次真实访问按令牌补齐资料，角色关联原样保留
        Assert.Equal(("carol", "carol@example.test", true), await UserAsync(connection, first));
        Assert.Equal(1, await AdminMembershipsAsync(connection, first, deleted: false));

        // 经正式接口从 Admin 角色撤掉整个角色管理子树（保留子项时父项会随之保留），下一请求即被拒
        var adminRoleId = await AdminRoleIdAsync(connection);
        var grants = await client.GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{adminRoleId}");
        Assert.NotNull(grants);
        var kept = grants.Grants
            .Where(g => g.Granted && g.Name != PermissionConstant.Roles.Default && !g.Name.StartsWith(PermissionConstant.Roles.Default + ".", StringComparison.Ordinal))
            .Select(g => g.Name)
            .ToArray();
        using (var revoke = await client.PutAsJsonAsync($"/api/v1/permissions/grants/roles/{adminRoleId}",
            new { expectedVersion = grants.Version, permissionNames = kept }))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }
        Assert.Equal(grants.Version + 1, await AdminRoleVersionAsync(connection));
        using (var denied = await client.GetAsync("/api/v1/roles"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // 再引导另一个人：如实预演缩减后的 K 项，施加时不补齐，新管理员同样按缩减后的角色判权
        var second = Guid.NewGuid();
        var reduced = await AdminRoleGrantCountAsync(connection);
        Assert.Equal(kept.Length, reduced);
        var dry = await RunAsync(connection, "--grant-admin", second.ToString());
        Assert.True(dry.Exit == 0, dry.Output);
        Assert.Contains($"keep the Admin role's current {reduced} permissions", dry.Output);
        var secondApplied = await RunAsync(connection, "--grant-admin", second.ToString(), "--apply");
        Assert.True(secondApplied.Exit == 0, secondApplied.Output);
        Assert.Equal(reduced, await AdminRoleGrantCountAsync(connection));
        Assert.Equal(0, await UserGrantCountAsync(connection, second));

        using var secondClient = ProjectWebApplicationFactory.CreateProjectClient(host);
        secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(second, key, "dave", "dave@example.test"));
        using (var secondDenied = await secondClient.GetAsync("/api/v1/roles"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, secondDenied.StatusCode);
        }

        // 对已是成员的人重跑，撤掉的权限也不回来
        Assert.Equal(0, (await RunAsync(connection, "--grant-admin", first.ToString(), "--apply")).Exit);
        using (var stillDenied = await client.GetAsync("/api/v1/roles"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);
        }
    }

    /// <summary>租户锁死的各种方式（停用、删除、移出 Admin、Admin 权限撤空及其组合），显式恢复一次即可重新管理。</summary>
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, true)]
    public async Task Restore_mode_brings_a_locked_out_administrator_back_in_one_run(
        bool disable, bool delete, bool revokeMembership, bool clearGrants)
    {
        using var factory = new ProjectWebApplicationFactory { UseProductionAuthentication = true };
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "restore-test" };
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Authentication:Audience", "resource-api")
            .ConfigureTestServices(services => services.Configure<OpenIddictValidationOptions>(options =>
            {
                options.Configuration = new OpenIddictConfiguration { Issuer = new Uri(Issuer) };
                options.Configuration.SigningKeys.Add(key);
            })));
        var connection = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")!;
        var subject = Guid.NewGuid();
        Assert.Equal(0, (await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply")).Exit);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject, key, "erin", "erin@example.test"));
        Assert.Equal(HttpStatusCode.OK, await RolesStatusAsync(client));

        // 管理员把自己所在的 Admin 角色权限撤空：经正式接口，授权版本随之前进
        if (clearGrants)
        {
            var adminRoleId = await AdminRoleIdAsync(connection);
            var grants = await client.GetFromJsonAsync<PermissionGrantsResponse>($"/api/v1/permissions/grants/roles/{adminRoleId}");
            using var cleared = await client.PutAsJsonAsync($"/api/v1/permissions/grants/roles/{adminRoleId}",
                new { expectedVersion = grants!.Version, permissionNames = Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }
        if (disable)
            await ExecuteAsync(connection, $"UPDATE {Schema}.\"Users\" SET \"IsActive\" = false WHERE \"Id\" = @subject", subject);
        if (delete)
            await ExecuteAsync(connection, $"UPDATE {Schema}.\"Users\" SET \"IsDeleted\" = true, \"DeletionTime\" = now() WHERE \"Id\" = @subject", subject);
        if (revokeMembership)
            await ExecuteAsync(connection, $"UPDATE {Schema}.\"UserRoles\" SET \"IsDeleted\" = true, \"DeletionTime\" = now() WHERE \"UserId\" = @subject", subject);
        Assert.NotEqual(HttpStatusCode.OK, await RolesStatusAsync(client));

        // 普通引导不推翻这些决定
        var plain = await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply");
        Assert.True(plain.Exit == (delete ? 1 : 0), plain.Output);
        Assert.NotEqual(HttpStatusCode.OK, await RolesStatusAsync(client));

        var damaged = await StateAsync(connection);
        var dry = await RunAsync(connection, "--grant-admin", subject.ToString(), "--restore");
        Assert.True(dry.Exit == 0, dry.Output);
        Assert.Contains("Dry run (restore)", dry.Output);
        Assert.Equal(damaged, await StateAsync(connection));

        var applied = await RunAsync(connection, "--grant-admin", subject.ToString(), "--restore", "--apply");
        Assert.True(applied.Exit == 0, applied.Output);
        Assert.Contains("Restored administrator access", applied.Output);
        Assert.Equal(HttpStatusCode.OK, await RolesStatusAsync(client));
        Assert.Equal(1, await AdminMembershipsAsync(connection, subject, deleted: false));
        // 权限补在共享的 Admin 角色上，不给这个人写直接授予
        Assert.Equal(0, await UserGrantCountAsync(connection, subject));
#if (IncludeOperationRecords)
        Assert.Equal(damaged.Records + 1, (await StateAsync(connection)).Records);
#else
        Assert.Contains("resource.admin-restored", applied.Output);
#endif

        var restored = await StateAsync(connection);
        var again = await RunAsync(connection, "--grant-admin", subject.ToString(), "--restore", "--apply");
        Assert.True(again.Exit == 0, again.Output);
        Assert.Contains("nothing to restore", again.Output);
        Assert.Equal(restored, await StateAsync(connection));
    }

    /// <summary>恢复中途失败整体回滚：撤回删除、启用、成员关系与前面已补的授予都不留下。</summary>
    [Fact]
    public async Task A_failed_restore_rolls_back_every_change()
    {
        var connection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var subject = Guid.NewGuid();
            Assert.Equal(0, (await RunAsync(connection, "--grant-admin", subject.ToString(), "--apply")).Exit);
            await ExecuteAsync(connection, $"UPDATE {Schema}.\"Users\" SET \"IsActive\" = false, \"IsDeleted\" = true, \"DeletionTime\" = now() WHERE \"Id\" = @subject", subject);
            await ExecuteAsync(connection, $"UPDATE {Schema}.\"UserRoles\" SET \"IsDeleted\" = true, \"DeletionTime\" = now() WHERE \"UserId\" = @subject", subject);
            await ExecuteAsync(connection, $"DELETE FROM {Schema}.\"PermissionGrantRecords\" WHERE \"ProviderName\" = 'Role'");
            // 拒绝最后一项补授：此前用户状态、成员关系与前面的授予都已写进同一事务
            var last = LastAvailablePermission(MultiTenancySides.Host);
            await ExecuteAsync(connection,
                $"ALTER TABLE {Schema}.\"PermissionGrantRecords\" ADD CONSTRAINT reject_restore CHECK (\"PermissionName\" <> '{last}') NOT VALID");
            var before = await StateAsync(connection);

            var applied = await RunAsync(connection, "--grant-admin", subject.ToString(), "--restore", "--apply");

            Assert.NotEqual(0, applied.Exit);
            Assert.Equal(before, await StateAsync(connection));
            Assert.Equal(0, await AdminMembershipsAsync(connection, subject, deleted: false));
            Assert.Equal(1, await ScalarAsync(connection, $"SELECT count(*) FROM {Schema}.\"Users\" WHERE \"Id\" = @subject AND \"IsDeleted\" AND NOT \"IsActive\"", subject));
        }
        finally
        {
            PostgreSqlTestDatabase.DropDatabase(connection);
        }
    }

    [Fact]
    public async Task Restore_requires_grant_admin()
    {
        var result = await RunAsync("Host=unused", "--restore", "--apply");

        Assert.Equal(2, result.Exit);
        Assert.Contains("--restore requires --grant-admin", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_initialization_lock_is_held_until_the_bootstrap_transaction_commits(bool restore)
    {
        // 判据是"提交前锁仍被持有、另一入口进不来"，而不是"并发后没有重复角色"：
        // 后者取决于调度，提前放锁的实现也可能碰巧串行通过。
        // 在进程内用真实数据库组装与命令相同的服务，设两个观察点：成功审计之后暂停一次，让启动入口排上队；
        // 再在目标事务真正提交前暂停一次，确认锁从未释放、启动入口仍在锁外——"审计后、提交前放锁"的实现在第二处失败。
        var connection = PostgreSqlTestDatabase.CreateDatabase();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new PausingCommit();
        IHost? host = null;
        Task? bootstrap = null;
        Task? startup = null;
        try
        {
            var probe = new InitializationLockProbe();
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host = BootstrapHost(connection, services =>
            {
                services.RemoveAll<IDistributedLock>();
                services.AddSingleton<IDistributedLock>(probe);
                var recorder = services.Last(d => d.ServiceType == typeof(IOperationRecorder));
                services.Remove(recorder);
                services.AddTransient<IOperationRecorder>(sp => new PausingRecorder(
                    (IOperationRecorder)ActivatorUtilities.CreateInstance(sp, recorder.ImplementationType!), reached, resume.Task, commit));
                services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(commit));
            });
            await host.StartAsync();

            var subject = Guid.NewGuid();
            var running = Task.Run(async () =>
            {
                commit.MarkTargetFlow();
                await using var scope = host.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ResourceAdminBootstrapRunner>().RunAsync(subject, null, apply: true, restore);
            });
            bootstrap = running;
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, probe.Holders);

            // 应用启动的初始化入口此时必须在锁外等待：它若进得来，就读不到尚未提交的角色而再建一套
            startup = Task.Run(async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISystemInitializer>().InitializeAsync();
            });
            // 启动入口自身出错时先暴露它的异常，而不是表现为等待超时
            await Task.WhenAny(probe.WaitingAsync(), startup).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(startup.IsCompleted, startup.Exception?.ToString());

            // 放行审计后，引导的事务停在提交前：此刻锁仍是引导那一次持有、从未释放，启动入口仍在锁外
            resume.SetResult();
            await Task.WhenAny(commit.Reached, running).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(commit.Reached.IsCompleted, $"The bootstrap did not reach its commit: {running.Exception}");
            Assert.True(probe.Releases == 0, "The initialization lock was released before the bootstrap transaction committed");
            Assert.Equal(1, probe.Holders);
            Assert.False(startup.IsCompleted, startup.Exception?.ToString());
            // 暂停点确实在提交之前：库外还看不到这次的成员关系
            Assert.Equal(0, await AdminMembershipsAsync(connection, subject, deleted: false));

            commit.Resume();
            Assert.Contains(restore ? "Restored administrator access" : "Assigned the Admin role", await running.WaitAsync(TimeSpan.FromSeconds(30)));
            await startup.WaitAsync(TimeSpan.FromSeconds(30));
            await host.StopAsync();

            Assert.Equal(1, await ScalarAsync(connection, $"SELECT count(*) FROM {Schema}.\"Roles\" WHERE \"Name\" = 'Admin'"));
            Assert.Equal(1, await ScalarAsync(connection, $"SELECT count(*) FROM {Schema}.\"Roles\" WHERE \"Name\" = 'Member'"));
            Assert.Equal(1, await AdminMembershipsAsync(connection, subject, deleted: false));
            Assert.Equal(1, await AdminRoleVersionAsync(connection));
        }
        finally
        {
            // 断言在放行前失败时（例如注回"提前放锁"），引导仍停在暂停处：先放行、等两个入口退出，
            // 再释放宿主与删库。清理中的异常不覆盖原断言。
            resume.TrySetResult();
            commit.Resume();
            try
            {
                await Task.WhenAll(new[] { bootstrap, startup }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch
            {
                // 任务自身的失败已由上面的断言报告
            }

            host?.Dispose();
            PostgreSqlTestDatabase.DropDatabase(connection);
        }
    }
#if (IncludeOperationRecords)

    [Fact]
    public async Task A_rejected_audit_insert_rolls_back_the_subject_role_and_grants()
    {
        var connection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var before = await StateAsync(connection);
            await ExecuteAsync(connection,
                $"ALTER TABLE {Schema}.\"OperationRecords\" ADD CONSTRAINT reject_bootstrap CHECK (\"Action\" <> 'resource.admin-granted')");
            var applied = await RunAsync(connection, "--grant-admin", Guid.NewGuid().ToString(), "--apply");
            Assert.NotEqual(0, applied.Exit);
            Assert.Equal(before, await StateAsync(connection));
        }
        finally { PostgreSqlTestDatabase.DropDatabase(connection); }
    }
#endif

    private static IHost BootstrapHost(string connection, Action<IServiceCollection> configure)
    {
        // 与命令的组合一致（Program.cs 的 --grant-admin 分支，非生产环境开启容器校验），只替换锁与审计记录器以便观测
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connection,
            ["ConnectionStrings:Redis"] = "",
#if (IncludeMultiTenancy)
            ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.example",
#endif
        });
        builder.ConfigureContainer(new DynamicProxyServiceRegistrationCallbackFactory(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        MigratorDependencyInjection.AddResourceAdminBootstrapServices(builder.Services, builder.Configuration);
        configure(builder.Services);
        return builder.Build();
    }

    private static string Token(Guid subject, SecurityKey key, string username, string email)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = "resource-api", TokenType = "at+jwt",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject.ToString(), ["jti"] = Guid.NewGuid().ToString(),
                ["preferred_username"] = username, ["email"] = email
            },
            IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-5), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });

    private static Task<long> AdminRoleVersionAsync(string connection)
        => ScalarAsync(connection,
            $"SELECT v.\"Version\" FROM {Schema}.\"AuthorizationVersionRecord\" v JOIN {Schema}.\"Roles\" r ON v.\"ProviderKey\" = r.\"Id\"::text "
            + "WHERE v.\"ProviderName\" = 'Role' AND r.\"Name\" = 'Admin'");

    private static async Task<HttpStatusCode> RolesStatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/roles");
        return response.StatusCode;
    }

    private static string LastAvailablePermission(MultiTenancySides side)
    {
        using var services = new ServiceCollection().AddLogging().AddApplicationServices().BuildServiceProvider();
        var definitions = services.GetRequiredService<IPermissionDefinitionManager>();
        return definitions.GetAll().Select(definition => definition.Name).Last(name => definitions.IsAvailableOn(name, side));
    }

    private static async Task<Guid> AdminRoleIdAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT \"Id\" FROM {Schema}.\"Roles\" WHERE \"Name\" = 'Admin'";
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(long Users, long Roles, long Memberships, long Grants, long Versions, long Records)> StateAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT (SELECT count(*) FROM {Schema}.\"Users\"), "
            + $"(SELECT count(*) FROM {Schema}.\"Roles\"), "
            + $"(SELECT count(*) FROM {Schema}.\"UserRoles\"), "
            + $"(SELECT count(*) FROM {Schema}.\"PermissionGrantRecords\"), "
            + $"(SELECT count(*) FROM {Schema}.\"AuthorizationVersionRecord\"), "
#if (IncludeOperationRecords)
            + $"(SELECT count(*) FROM {Schema}.\"OperationRecords\")";
#else
            + "0::bigint";
#endif
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
    }

    private static async Task<(string Username, string Email, bool IsActive)> UserAsync(string connection, Guid subject)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT \"Username\", \"Email\", \"IsActive\" FROM {Schema}.\"Users\" WHERE \"Id\" = @subject";
        command.Parameters.AddWithValue("subject", subject);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2));
    }

    private static Task<long> AdminMembershipsAsync(string connection, Guid subject, bool deleted)
        => ScalarAsync(connection,
            $"SELECT count(*) FROM {Schema}.\"UserRoles\" ur JOIN {Schema}.\"Roles\" r ON r.\"Id\" = ur.\"RoleId\" "
            + $"WHERE ur.\"UserId\" = @subject AND r.\"Name\" = 'Admin' AND ur.\"IsDeleted\" = {(deleted ? "true" : "false")}",
            subject);

    private static Task<long> UserGrantCountAsync(string connection, Guid subject)
        => ScalarAsync(connection,
            $"SELECT count(*) FROM {Schema}.\"PermissionGrantRecords\" WHERE \"ProviderName\" = 'User' AND \"ProviderKey\" = @key",
            subject, asText: true);

    private static Task<long> AdminRoleGrantCountAsync(string connection)
        => ScalarAsync(connection,
            $"SELECT count(*) FROM {Schema}.\"PermissionGrantRecords\" g JOIN {Schema}.\"Roles\" r ON g.\"ProviderKey\" = r.\"Id\"::text "
            + "WHERE g.\"ProviderName\" = 'Role' AND r.\"Name\" = 'Admin'");

    private static async Task<long> ScalarAsync(string connection, string sql, Guid? subject = null, bool asText = false)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        if (subject is { } value)
        {
            command.Parameters.AddWithValue(asText ? "key" : "subject", asText ? value.ToString() : value);
        }
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connection, string sql, Guid? subject = null)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        if (subject is { } value)
        {
            command.Parameters.AddWithValue("subject", value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int Exit, string Output)> RunAsync(string connection, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CompanyName.ProjectName.IntegrationTests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CompanyName.ProjectName.IntegrationTests.deps.json"));
        start.ArgumentList.Add(typeof(DatabaseMigrationRunner).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["ConnectionStrings__Default"] = connection;
        start.Environment["ConnectionStrings__Redis"] = "";
#if (IncludeMultiTenancy)
        start.Environment["Leistd__ServiceClients__Identity__BaseAddress"] = "https://identity.example";
#endif
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }

    private sealed record PermissionGrantsResponse(long Version, PermissionGrantStateResponse[] Grants);

    private sealed record PermissionGrantStateResponse(string Name, bool Granted);

    /// <summary>真实互斥的初始化锁替身：暴露当前持有数，并在有人排队等待时发出信号。</summary>
    private sealed class InitializationLockProbe : IDistributedLock
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int holders;
        private int releases;

        public int Holders => Volatile.Read(ref holders);

        /// <summary>释放次数；在释放的当下同步计数，不受等待方何时接手影响。</summary>
        public int Releases => Volatile.Read(ref releases);

        public Task WaitingAsync() => waiting.Task;

        public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SystemInitializer.InitializationLockKey, key);
            if (!gate.Wait(0))
            {
                waiting.TrySetResult();
                await gate.WaitAsync(cancellationToken);
            }

            Interlocked.Increment(ref holders);
            return new Handle(this);
        }

        public async Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
            => await LockAsync(key, cancellationToken);

        private sealed class Handle(InitializationLockProbe owner) : ILockHandle
        {
            public CancellationToken LockLost => CancellationToken.None;

            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref owner.holders);
                Interlocked.Increment(ref owner.releases);
                owner.gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>记完成功审计（提交前的最后一步写入）后暂停，直到测试放行；并让随后的目标事务提交也停一次。</summary>
    private sealed class PausingRecorder(IOperationRecorder inner, TaskCompletionSource reached, Task resume, PausingCommit commit) : IOperationRecorder
    {
        public async Task RecordSucceededAsync(string action, OperationTarget target, string authorizationBasis, CancellationToken cancellationToken = default)
        {
            await inner.RecordSucceededAsync(action, target, authorizationBasis, cancellationToken);
            commit.Arm();
            reached.TrySetResult();
            await resume;
        }

        public Task RecordFailedAsync(string action, OperationTarget target, string authorizationBasis, OperationFailure failure = default)
            => inner.RecordFailedAsync(action, target, authorizationBasis, failure);
    }

    /// <summary>
    /// 只暂停目标事务的提交：由 <see cref="MarkTargetFlow"/> 标记的执行流在成功审计之后发起的第一次提交，
    /// 停在真正提交之前，直到测试放行。其他执行流（竞争入口）的提交不受影响。
    /// </summary>
    private sealed class PausingCommit : DbTransactionInterceptor
    {
        private readonly AsyncLocal<bool> targetFlow = new();
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        public Task Reached => reached.Task;

        public void MarkTargetFlow() => targetFlow.Value = true;

        public void Arm()
        {
            if (targetFlow.Value)
                Volatile.Write(ref armed, 1);
        }

        public void Resume() => resume.TrySetResult();

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (targetFlow.Value && Interlocked.Exchange(ref armed, 0) == 1)
            {
                reached.TrySetResult();
                await resume.Task;
            }

            return result;
        }
    }
}
#endif
