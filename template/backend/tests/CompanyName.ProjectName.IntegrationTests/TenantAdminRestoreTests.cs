#if (LocalIdentity && IncludeMultiTenancy)
extern alias Migrator;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Tenants.Dtos;
using CompanyName.ProjectName.Application.Tenants.Provider;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Data.Querying;
using Leistd.Ddd.Domain.Repositories;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Management.Provisioning;
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Tenancy;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.UnitOfWork;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using DatabaseMigrationRunner = Migrator::CompanyName.ProjectName.DbMigrator.Runners.DatabaseMigrationRunner;
using MigratorDependencyInjection = Migrator::CompanyName.ProjectName.DbMigrator.DependencyInjection;
using TenantAdminRestoreRunner = Migrator::CompanyName.ProjectName.DbMigrator.Runners.TenantAdminRestoreRunner;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>租户管理员恢复命令：真实 PostgreSQL、真实 CLI；每种锁死执行一次即恢复，恢复后实际登录并调用管理接口。</summary>
public sealed class TenantAdminRestoreTests
{
    private const string Schema = $"\"{DatabaseSchema.Name}\"";
    private const string TenantAdminPassword = "Tenant@123456";

    /// <summary>租户把自己锁死的方式，可组合。</summary>
    [Flags]
    public enum Damage
    {
        Disabled = 1,
        Deleted = 2,
        MembershipRevoked = 4,
        GrantsCleared = 8,
        All = Disabled | Deleted | MembershipRevoked | GrantsCleared
    }

    [Theory]
    [InlineData(Damage.Disabled)]
    [InlineData(Damage.Deleted)]
    [InlineData(Damage.MembershipRevoked)]
    [InlineData(Damage.GrantsCleared)]
    [InlineData(Damage.All)]
    public async Task One_run_restores_the_original_administrator_who_can_then_manage_the_tenant(Damage damage)
    {
        using var env = new RestoreHost();
        var tenantId = await CreateTenantAsync(env.Host, "restore" + Guid.NewGuid().ToString("N")[..8]);
        var adminId = await AdminIdAsync(env.Connection, tenantId);
        Assert.True(await CanManageAsync(env.Host, tenantId));
        if (damage == Damage.All)
        {
            // 管理员另有一个角色：成员关系全撤之后，恢复只补回 Admin 这一条
            await AssignMemberRoleAsync(env.Host, tenantId);
        }

        await DamageAsync(env.Host, tenantId, damage);
        Assert.False(await CanManageAsync(env.Host, tenantId));

        var damaged = await StateAsync(env.Connection, tenantId);
        var dry = await RunAsync(env, tenantId);
        Assert.True(dry.Exit == 0, dry.Output);
        Assert.Contains("Dry run", dry.Output);
        Assert.Equal(damaged, await StateAsync(env.Connection, tenantId));

        var applied = await RunAsync(env, tenantId, "--apply");
        Assert.True(applied.Exit == 0, applied.Output);
        Assert.Contains("Restored the administrator", applied.Output);
        var restored = await StateAsync(env.Connection, tenantId);

        // 原记录按原 Id 回来（不新建用户），原口令照常登录（不重置），再调用一个管理接口
        Assert.Equal(damaged.Users, restored.Users);
        Assert.Equal(adminId, await AdminIdAsync(env.Connection, tenantId));
        Assert.True(await CanManageAsync(env.Host, tenantId));
#if (IncludeOperationRecords)
        Assert.Equal(damaged.Records + 1, restored.Records);
        Assert.Equal(1, await ScalarAsync(env.Connection,
            $"SELECT count(*) FROM {Schema}.\"OperationRecords\" WHERE \"TenantId\" = @tenant AND \"Action\" = 'tenant.admin-restored' "
            + "AND \"AuthorizationBasis\" = 'DeploymentBootstrap' AND \"ActorId\" LIKE 'deployment:%'", tenantId));
#else
        Assert.Contains("tenant.admin-restored", applied.Output);
#endif
        if (damage == Damage.All)
        {
            Assert.Equal(0, await ScalarAsync(env.Connection,
                $"SELECT count(*) FROM {Schema}.\"UserRoles\" ur JOIN {Schema}.\"Roles\" r ON r.\"Id\" = ur.\"RoleId\" "
                + $"WHERE ur.\"TenantId\" = @tenant AND r.\"Name\" = '{RoleDomainService.MemberRoleName}' AND NOT ur.\"IsDeleted\"", tenantId));
        }

        // 再跑一次什么也不写，也不再留记录
        var settled = await StateAsync(env.Connection, tenantId);
        var again = await RunAsync(env, tenantId, "--apply");
        Assert.True(again.Exit == 0, again.Output);
        Assert.Contains("nothing to restore", again.Output);
        Assert.Equal(settled, await StateAsync(env.Connection, tenantId));
    }

    /// <summary>独立库租户在它自己的库里恢复；共享库里的另一个租户不受牵连，宿主库也不出现前者的数据。</summary>
    [Fact]
    public async Task A_dedicated_tenant_is_restored_in_its_own_database_without_touching_other_tenants()
    {
        using var env = new RestoreHost();
        var dedicatedConnection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var dedicated = await CreateTenantAsync(env.Host, "dedicated" + Guid.NewGuid().ToString("N")[..8], dedicatedConnection);
            var shared = await CreateTenantAsync(env.Host, "shared" + Guid.NewGuid().ToString("N")[..8]);
            await DamageAsync(env.Host, dedicated, Damage.All);
            await DamageAsync(env.Host, shared, Damage.All);
            Assert.Equal(0, await ScalarAsync(env.Connection, $"SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant", dedicated));
            var sharedDamaged = await StateAsync(env.Connection, shared);

            var applied = await RunAsync(env, dedicated, "--apply");
            Assert.True(applied.Exit == 0, applied.Output);

            // 先比状态再试登录：失败的登录本身会留一条记录
            Assert.Equal(sharedDamaged, await StateAsync(env.Connection, shared));
            Assert.True(await CanManageAsync(env.Host, dedicated));
            Assert.False(await CanManageAsync(env.Host, shared));
            Assert.Equal(0, await ScalarAsync(env.Connection, $"SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant", dedicated));
            Assert.Equal(0, await ScalarAsync(dedicatedConnection, $"SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant", shared));

            var dedicatedRestored = await StateAsync(dedicatedConnection, dedicated);
            var sharedApplied = await RunAsync(env, shared, "--apply");
            Assert.True(sharedApplied.Exit == 0, sharedApplied.Output);
            Assert.True(await CanManageAsync(env.Host, shared));
            Assert.Equal(dedicatedRestored, await StateAsync(dedicatedConnection, dedicated));
        }
        finally
        {
            PostgreSqlTestDatabase.DropDatabase(dedicatedConnection);
        }
    }

    /// <summary>中途失败整体回滚：撤回删除、启用、成员关系与已补的授予一样都不留。</summary>
    [Fact]
    public async Task A_failure_part_way_rolls_back_every_change()
    {
        using var env = new RestoreHost();
        var tenantId = await CreateTenantAsync(env.Host, "rollback" + Guid.NewGuid().ToString("N")[..8]);
        await DamageAsync(env.Host, tenantId, Damage.All);
        // 拒绝最后一项补授：此前用户状态、成员关系与前面的授予都已写进同一事务
        var definitions = env.Host.Services.GetRequiredService<IPermissionDefinitionManager>();
        var last = definitions.GetAll().Select(definition => definition.Name)
            .Last(name => definitions.IsAvailableOn(name, MultiTenancySides.Tenant));
        await ExecuteAsync(env.Connection,
            $"ALTER TABLE {Schema}.\"PermissionGrantRecords\" ADD CONSTRAINT reject_restore CHECK (\"PermissionName\" <> '{last}') NOT VALID");
        var before = await StateAsync(env.Connection, tenantId);

        var applied = await RunAsync(env, tenantId, "--apply");

        Assert.NotEqual(0, applied.Exit);
        Assert.Equal(before, await StateAsync(env.Connection, tenantId));
        Assert.False(await CanManageAsync(env.Host, tenantId));
    }

    /// <summary>原管理员记录不在（改了名或被永久清除）就失败，不替人新建账号；不存在的租户同样失败。两者都不写任何东西。</summary>
    [Fact]
    public async Task Without_the_original_record_or_the_tenant_the_command_fails_and_changes_nothing()
    {
        using var env = new RestoreHost();
        var tenantId = await CreateTenantAsync(env.Host, "renamed" + Guid.NewGuid().ToString("N")[..8]);
        await ExecuteAsync(env.Connection,
            $"UPDATE {Schema}.\"Users\" SET \"Username\" = 'renamed-admin' WHERE \"TenantId\" = @tenant AND \"Username\" = '{AdminConstant.TenantAdminUsername}'",
            tenantId);
        var before = await StateAsync(env.Connection, tenantId);

        var missingAdmin = await RunAsync(env, tenantId, "--apply");
        Assert.NotEqual(0, missingAdmin.Exit);
        Assert.Contains("never creates accounts", missingAdmin.Output);
        Assert.Equal(before, await StateAsync(env.Connection, tenantId));

        var unknown = await RunAsync(env, Guid.NewGuid(), "--apply");
        Assert.NotEqual(0, unknown.Exit);
        Assert.Contains("does not exist", unknown.Output);
    }

    /// <summary>恢复不碰凭据与会话：已启用的两步验证保留，撤销过的会话不复活。</summary>
    [Fact]
    public async Task Restore_keeps_two_factor_enabled_and_does_not_revive_revoked_sessions()
    {
        using var env = new RestoreHost();
        var tenantId = await CreateTenantAsync(env.Host, "twofactor" + Guid.NewGuid().ToString("N")[..8]);
        using var earlier = await LoginAsync(env.Host, tenantId);
        Assert.Equal(HttpStatusCode.OK, await UsersStatusAsync(earlier));

        await InTenantAsync(env.Host, tenantId, async services =>
        {
            var admin = await LoadAdminAsync(services);
            admin.EnableTwoFactor("unused-for-restore", [], 0);
            admin.Disable();
            await services.GetRequiredService<IUserRepository>().UpdateAsync(admin);
            Assert.Equal(1, await services.GetRequiredService<UserSessionDomainService>().RevokeAllAsync(admin.Id, null));
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await UsersStatusAsync(earlier));

        var applied = await RunAsync(env, tenantId, "--apply");
        Assert.True(applied.Exit == 0, applied.Output);
        Assert.Contains("enable the account", applied.Output);

        Assert.Equal(HttpStatusCode.Unauthorized, await UsersStatusAsync(earlier));
        Assert.Equal(0, await ScalarAsync(env.Connection,
            $"SELECT count(*) FROM {Schema}.\"UserSessions\" WHERE \"TenantId\" = @tenant", tenantId));
        await InTenantAsync(env.Host, tenantId, async services =>
        {
            var admin = await LoadAdminAsync(services);
            Assert.True(admin.IsActive);
            Assert.True(admin.TwoFactorEnabled);
        });
        // 口令登录照常停在第二步，不直接签发会话
        using var client = TenantClient(env.Host, tenantId);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/session-login",
            new { UsernameOrEmail = AdminConstant.TenantAdminUsername, Password = TenantAdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
    }

    /// <summary>恢复与租户开通共用该租户的初始化锁，并持有到提交之后：开通在此期间必须在锁外等待。</summary>
    [Fact]
    public async Task The_tenant_initialization_lock_is_held_until_the_restore_commits()
    {
        // 与资源服务引导的持锁测试同一判据："提交前锁仍被持有、竞争入口进不来"，不看并发后的结果。
        // 两个观察点：成功审计之后暂停一次，让开通入口排上队；再在目标事务真正提交前暂停一次，
        // 确认锁从未释放、开通仍在锁外——"审计后、提交前放锁"的实现在第二处失败。
        // 锁替身只对该租户的初始化键互斥：恢复若换了别的键，开通就不会被挡住
        var probe = new TenantLockProbe();
        using var env = new RestoreHost(services =>
        {
            services.RemoveAll<IDistributedLock>();
            services.AddSingleton<IDistributedLock>(probe);
        });
        var tenantId = await CreateTenantAsync(env.Host, "locked" + Guid.NewGuid().ToString("N")[..8]);
        await DamageAsync(env.Host, tenantId, Damage.Disabled | Damage.GrantsCleared);
        probe.Key = TenantSeeder.InitializationLockKey(tenantId);

        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new PausingCommit();
        Task<string>? restore = null;
        Task? provision = null;
        using var command = RestoreCommandHost(env, services =>
        {
            services.RemoveAll<IDistributedLock>();
            services.AddSingleton<IDistributedLock>(probe);
            var recorder = services.Last(d => d.ServiceType == typeof(IOperationRecorder));
            services.Remove(recorder);
            services.AddTransient<IOperationRecorder>(sp => new PausingRecorder(
                (IOperationRecorder)ActivatorUtilities.CreateInstance(sp, recorder.ImplementationType!), reached, resume.Task, commit));
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(commit));
        });
        try
        {
            await command.StartAsync();
            restore = Task.Run(async () =>
            {
                commit.MarkTargetFlow();
                await using var scope = command.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<TenantAdminRestoreRunner>().RunAsync(tenantId, apply: true);
            });
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, probe.Holders);

            // API 里的开通入口此时必须在锁外等待
            provision = ProvisionAgainAsync(env.Host, tenantId);
            // 开通自身出错时先暴露它的异常，而不是表现为等待超时
            await Task.WhenAny(probe.WaitingAsync(), provision).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(provision.IsCompleted, provision.Exception?.ToString());

            // 放行审计后，恢复的事务停在提交前：此刻锁仍是恢复那一次持有、从未释放，开通仍在锁外
            resume.SetResult();
            await Task.WhenAny(commit.Reached, restore).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(commit.Reached.IsCompleted, $"The restore did not reach its commit: {restore.Exception}");
            Assert.True(probe.Releases == 0, "The tenant initialization lock was released before the restore transaction committed");
            Assert.Equal(1, probe.Holders);
            Assert.False(provision.IsCompleted, provision.Exception?.ToString());
            // 暂停点确实在提交之前：库外还看不到恢复的写入
            Assert.Equal(0, await ScalarAsync(env.Connection,
                $"SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant AND \"IsActive\"", tenantId));

            commit.Resume();
            Assert.Contains("Restored the administrator", await restore.WaitAsync(TimeSpan.FromSeconds(30)));
            await provision.WaitAsync(TimeSpan.FromSeconds(30));
            await command.StopAsync();

            Assert.True(await CanManageAsync(env.Host, tenantId));
            Assert.Equal(1, await ScalarAsync(env.Connection,
                $"SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant", tenantId));
        }
        finally
        {
            // 断言在放行前失败时（例如注回"提前放锁"），恢复仍停在暂停处：先放行、等两个入口退出，再释放宿主。
            // 清理中的异常不覆盖原断言
            resume.TrySetResult();
            commit.Resume();
            try
            {
                await Task.WhenAll(new[] { restore, provision }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch
            {
                // 任务自身的失败已由上面的断言报告
            }
        }
    }

    private static async Task<Guid> CreateTenantAsync(WebApplicationFactory<Program> host, string name, string? dedicatedConnection = null)
    {
        using var hostAdmin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        object[] connections = dedicatedConnection is null ? [] : [new { Name = "default", ConnectionString = dedicatedConnection }];
        using var response = await hostAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            AdminEmail = $"admin@{name}.example.com",
            AdminPassword = TenantAdminPassword,
            Connections = connections
        });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>以原口令登录租户管理员并调用一个需要管理权限的接口。</summary>
    private static async Task<bool> CanManageAsync(WebApplicationFactory<Program> host, Guid tenantId)
    {
        using var client = await LoginAsync(host, tenantId);
        return client is not null && await UsersStatusAsync(client) == HttpStatusCode.OK;
    }

    /// <summary>以原口令登录租户管理员，返回带会话 Cookie 的客户端；登录未直接签发会话时返回 null。</summary>
    private static async Task<HttpClient?> LoginAsync(WebApplicationFactory<Program> host, Guid tenantId)
    {
        var client = TenantClient(host, tenantId);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/session-login",
            new { UsernameOrEmail = AdminConstant.TenantAdminUsername, Password = TenantAdminPassword });
        if (login.StatusCode != HttpStatusCode.OK || !login.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            client.Dispose();
            return null;
        }

        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(value => value.Split(';', 2)[0])));
        return client;
    }

    private static HttpClient TenantClient(WebApplicationFactory<Program> host, Guid tenantId)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        return client;
    }

    private static async Task<HttpStatusCode> UsersStatusAsync(HttpClient? client)
    {
        Assert.NotNull(client);
        using var users = await client.GetAsync("/api/v1/users?offset=0&limit=10");
        return users.StatusCode;
    }

    /// <summary>经 API 的开通入口对已开通的租户再跑一次开通（与创建租户同一工作单元形态），它要取同一把初始化锁。</summary>
    private static Task ProvisionAgainAsync(WebApplicationFactory<Program> host, Guid tenantId) => Task.Run(async () =>
    {
        await using var scope = host.Services.CreateAsyncScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantStore>().FindAsync(tenantId);
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        using (var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true))
        {
            await scope.ServiceProvider.GetRequiredService<ITenantProvisioner>().ProvisionAsync(new TenantProvisioningContext(tenant!,
                new CreateTenantWithAdminInputDto { Name = tenant!.Name, AdminEmail = "unused@example.com", AdminPassword = TenantAdminPassword }));
            await uow.CompleteAsync();
        }
    });

    /// <summary>与命令的组合一致（Program.cs 的 --restore-tenant-admin 分支，非生产环境开启容器校验），由测试替换部分服务以便观测。</summary>
    private static IHost RestoreCommandHost(RestoreHost env, Action<IServiceCollection> configure)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = env.Connection,
            ["ConnectionStrings:Redis"] = "",
            ["DataProtection:KeysPath"] = env.KeysPath,
        });
        builder.ConfigureContainer(new DynamicProxyServiceRegistrationCallbackFactory(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        MigratorDependencyInjection.AddTenantAdminRestoreServices(builder.Services, builder.Configuration, builder.Environment);
        configure(builder.Services);
        return builder.Build();
    }

    private static async Task AssignMemberRoleAsync(WebApplicationFactory<Program> host, Guid tenantId)
    {
        await InTenantAsync(host, tenantId, async services =>
        {
            var member = await services.GetRequiredService<IRepository<Role, Guid>>()
                .GetFirstAsync(role => role.Name == RoleDomainService.MemberRoleName);
            var admin = await LoadAdminAsync(services);
            admin.AssignRoles([member!.Id]);
            await services.GetRequiredService<IUserRepository>().UpdateAsync(admin);
        });
    }

    /// <summary>按正式领域路径把管理员弄坏，写入落在该租户实际的库里。</summary>
    private static async Task DamageAsync(WebApplicationFactory<Program> host, Guid tenantId, Damage damage)
    {
        await InTenantAsync(host, tenantId, async services =>
        {
            var admin = await LoadAdminAsync(services);
            if (damage.HasFlag(Damage.GrantsCleared))
            {
                var adminRole = await services.GetRequiredService<IRepository<Role, Guid>>()
                    .GetFirstAsync(role => role.Name == AdminConstant.RoleName);
                await services.GetRequiredService<IPermissionGrantManager>()
                    .ReplaceGrantsAsync(PermissionGrantProviderNames.Role, adminRole!.Id.ToString(), []);
            }

            if (damage.HasFlag(Damage.Disabled)) admin.Disable();
            if (damage.HasFlag(Damage.MembershipRevoked)) admin.RemoveAllRoles();
            await services.GetRequiredService<IUserRepository>().UpdateAsync(admin);
        });
        if (damage.HasFlag(Damage.Deleted))
        {
            // 与用户管理的删除同一条路径：不带成员关系读取后软删除，成员关系随用户保留
            await InTenantAsync(host, tenantId, async services =>
            {
                var users = services.GetRequiredService<IUserRepository>();
                var admin = await users.GetFirstAsync(user => user.Username == AdminConstant.TenantAdminUsername);
                await users.DeleteAsync(admin!);
            });
        }
    }

    private static async Task<User> LoadAdminAsync(IServiceProvider services)
        => (await services.GetRequiredService<IQueryableAsyncExecuter>().FirstOrDefaultAsync(
            (await services.GetRequiredService<IUserRepository>().GetQueryableWithRolesAsync())
                .Where(user => user.Username == AdminConstant.TenantAdminUsername)))!;

    private static async Task InTenantAsync(WebApplicationFactory<Program> host, Guid tenantId, Func<IServiceProvider, Task> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        using (var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true))
        {
            await action(scope.ServiceProvider);
            await uow.CompleteAsync();
        }
    }

    private static async Task<Guid> AdminIdAsync(string connection, Guid tenantId)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT \"Id\" FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant AND \"Username\" = '{AdminConstant.TenantAdminUsername}' AND NOT \"IsDeleted\"";
        command.Parameters.AddWithValue("tenant", tenantId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>该租户在给定库里的全部授权相关状态；任何一项写入都会改变它。</summary>
    private static async Task<(long Users, long Active, long Deleted, long Memberships, long LiveMemberships, long Grants, long Versions, long Records)> StateAsync(
        string connection, Guid tenantId)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT (SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant), "
            + $"(SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant AND \"IsActive\"), "
            + $"(SELECT count(*) FROM {Schema}.\"Users\" WHERE \"TenantId\" = @tenant AND \"IsDeleted\"), "
            + $"(SELECT count(*) FROM {Schema}.\"UserRoles\" WHERE \"TenantId\" = @tenant), "
            + $"(SELECT count(*) FROM {Schema}.\"UserRoles\" WHERE \"TenantId\" = @tenant AND NOT \"IsDeleted\"), "
            + $"(SELECT count(*) FROM {Schema}.\"PermissionGrantRecords\" WHERE \"TenantId\" = @tenant), "
            + $"(SELECT coalesce(sum(\"Version\"), 0)::bigint FROM {Schema}.\"AuthorizationVersionRecord\" WHERE \"TenantId\" = @tenant), "
#if (IncludeOperationRecords)
            + $"(SELECT count(*) FROM {Schema}.\"OperationRecords\" WHERE \"TenantId\" = @tenant)";
#else
            + "0::bigint";
#endif
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    private static async Task<long> ScalarAsync(string connection, string sql, Guid tenantId)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connection, string sql, Guid? tenantId = null)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        if (tenantId is { } value)
            command.Parameters.AddWithValue("tenant", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int Exit, string Output)> RunAsync(RestoreHost env, Guid tenantId, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CompanyName.ProjectName.IntegrationTests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CompanyName.ProjectName.IntegrationTests.deps.json"));
        start.ArgumentList.Add(typeof(DatabaseMigrationRunner).Assembly.Location);
        start.ArgumentList.Add("--restore-tenant-admin");
        start.ArgumentList.Add(tenantId.ToString());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["ConnectionStrings__Default"] = env.Connection;
        start.Environment["ConnectionStrings__Redis"] = "";
        // 与 API 共用密钥环：独立库的连接串由 API 加密登记，命令要解得开
        start.Environment["DataProtection__KeysPath"] = env.KeysPath;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }

    /// <summary>测试宿主与命令共用一个库和一个密钥环目录。</summary>
    private sealed class RestoreHost : IDisposable
    {
        private readonly ProjectWebApplicationFactory factory = new();

        public RestoreHost(Action<IServiceCollection>? configureServices = null)
        {
            KeysPath = Path.Combine(Path.GetTempPath(), $"ProjectTests-restore-keys-{Guid.NewGuid():N}");
            Host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DataProtection:KeysPath", KeysPath);
                if (configureServices is not null)
                    builder.ConfigureTestServices(configureServices);
            });
            Connection = Host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")!;
        }

        public WebApplicationFactory<Program> Host { get; }

        public string KeysPath { get; }

        public string Connection { get; }

        public void Dispose()
        {
            Host.Dispose();
            factory.Dispose();
            if (Directory.Exists(KeysPath))
                Directory.Delete(KeysPath, recursive: true);
        }
    }

    /// <summary>只对 <see cref="Key"/> 真实互斥的锁替身：暴露当前持有数，并在有人排队等待时发出信号；其他键直接放行。</summary>
    private sealed class TenantLockProbe : IDistributedLock
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int holders;
        private int releases;

        public string? Key { get; set; }

        public int Holders => Volatile.Read(ref holders);

        /// <summary>目标键被释放的次数；在释放的当下同步计数，不受等待方何时接手影响。</summary>
        public int Releases => Volatile.Read(ref releases);

        public Task WaitingAsync() => waiting.Task;

        public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            if (key != Key)
                return new Handle(null);

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

        private sealed class Handle(TenantLockProbe? owner) : ILockHandle
        {
            public CancellationToken LockLost => CancellationToken.None;

            public ValueTask DisposeAsync()
            {
                if (owner is not null)
                {
                    Interlocked.Decrement(ref owner.holders);
                    Interlocked.Increment(ref owner.releases);
                    owner.gate.Release();
                }

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
