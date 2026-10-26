#if (LocalIdentity)
extern alias Migrator;
using System.Diagnostics;
#if (IncludeMultiTenancy)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
#endif
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Grants;
using Leistd.Ddd.Domain.Repositories;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.UnitOfWork;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using DatabaseMigrationRunner = Migrator::CompanyName.ProjectName.DbMigrator.Runners.DatabaseMigrationRunner;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>迁移后的 Admin 补齐：真实 PostgreSQL、真实 CLI；Admin 缺了权限（等同于新版本新增的权限），一次 <c>--apply</c> 补齐，重跑不再写入。</summary>
public sealed class AdminGrantTopUpTests
{
    private const string Schema = $"\"{DatabaseSchema.Name}\"";
#if (IncludeMultiTenancy)
    private const string TenantAdminPassword = "Tenant@123456";
#endif

    [Fact]
    public async Task The_host_admin_gets_its_missing_permissions_back_once_and_a_rerun_writes_nothing()
    {
        using var env = new CommandHost();
        var revoked = await RevokeFirstAsync(env.Host, tenantId: null);
        var damaged = await StateAsync(env.Connection, tenantId: null);

        var preview = await RunAsync(env);
        Assert.True(preview.Exit == 0, preview.Output);
        Assert.Contains("host: would grant", preview.Output);
        Assert.Contains(revoked, preview.Output);
        Assert.Equal(damaged, await StateAsync(env.Connection, tenantId: null));

        var applied = await RunAsync(env, "--apply");
        Assert.True(applied.Exit == 0, applied.Output);
        Assert.Contains("host: granted", applied.Output);
        Assert.Empty(await MissingAsync(env.Host, tenantId: null));
        var toppedUp = await StateAsync(env.Connection, tenantId: null);
        Assert.True(toppedUp.Versions > damaged.Versions);

        var rerun = await RunAsync(env, "--apply");
        Assert.True(rerun.Exit == 0, rerun.Output);
        Assert.Contains("already hold every available permission", rerun.Output);
        Assert.Equal(toppedUp, await StateAsync(env.Connection, tenantId: null));
    }

    // 其他角色要不要新权限由管理员决定：补齐只动 Admin，Member 照样缺着
    [Fact]
    public async Task Roles_other_than_admin_are_left_alone()
    {
        using var env = new CommandHost();
        await RevokeFirstAsync(env.Host, tenantId: null);
        var memberMissing = await MissingAsync(env.Host, tenantId: null, RoleDomainService.MemberRoleName);
        Assert.NotEmpty(memberMissing);

        var applied = await RunAsync(env, "--apply");

        Assert.True(applied.Exit == 0, applied.Output);
        Assert.Empty(await MissingAsync(env.Host, tenantId: null));
        Assert.Equal(memberMissing, await MissingAsync(env.Host, tenantId: null, RoleDomainService.MemberRoleName));
    }
#if (IncludeMultiTenancy)

    // 共享库的租户与独立库的租户各补租户侧，写在各自的库里；宿主 Admin 补宿主侧
    [Fact]
    public async Task Shared_and_dedicated_tenant_admins_are_topped_up_in_their_own_databases()
    {
        using var env = new CommandHost();
        var dedicatedConnection = PostgreSqlTestDatabase.CreateDatabase();
        try
        {
            var dedicated = await CreateTenantAsync(env.Host, "dedicated" + Guid.NewGuid().ToString("N")[..8], dedicatedConnection);
            var shared = await CreateTenantAsync(env.Host, "shared" + Guid.NewGuid().ToString("N")[..8]);
            var revokedDedicated = await RevokeFirstAsync(env.Host, dedicated);
            var revokedShared = await RevokeFirstAsync(env.Host, shared);
            await RevokeFirstAsync(env.Host, tenantId: null);

            var applied = await RunAsync(env, "--apply");

            Assert.True(applied.Exit == 0, applied.Output);
            Assert.Contains($"tenant {dedicated}: granted", applied.Output);
            Assert.Contains($"tenant {shared}: granted", applied.Output);
            Assert.Contains(revokedDedicated, applied.Output);
            Assert.Contains(revokedShared, applied.Output);
            Assert.Empty(await MissingAsync(env.Host, dedicated));
            Assert.Empty(await MissingAsync(env.Host, shared));
            Assert.Empty(await MissingAsync(env.Host, tenantId: null));
            // 独立库租户的授予只在它自己的库里
            Assert.Equal(0, await ScalarAsync(env.Connection,
                $"SELECT count(*) FROM {Schema}.\"PermissionGrantRecords\" WHERE \"TenantId\" = '{dedicated}'"));
            Assert.True(await ScalarAsync(dedicatedConnection,
                $"SELECT count(*) FROM {Schema}.\"PermissionGrantRecords\" WHERE \"TenantId\" = '{dedicated}'") > 0);
            // 租户 Admin 不会拿到宿主侧权限
            Assert.Empty(await HostOnlyGrantsAsync(env.Host, shared));
        }
        finally
        {
            PostgreSqlTestDatabase.DropDatabase(dedicatedConnection);
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

    private static Task<IReadOnlyList<string>> HostOnlyGrantsAsync(WebApplicationFactory<Program> host, Guid tenantId)
        => InScopeAsync(host, tenantId, async services =>
        {
            var definitions = services.GetRequiredService<Leistd.Authorization.Definitions.IPermissionDefinitionManager>();
            var grants = await services.GetRequiredService<IPermissionGrantStore>()
                .GetGrantsAsync(PermissionGrantProviderNames.Role, (await AdminRoleAsync(services)).Id.ToString());
            return (IReadOnlyList<string>)[.. grants.PermissionNames
                .Where(name => definitions.IsAvailableOn(name, MultiTenancySides.Host) && !definitions.IsAvailableOn(name, MultiTenancySides.Tenant))];
        });
#endif

    /// <summary>撤掉 Admin 的第一项可用权限（连同子孙），返回它的名字：Admin 缺权限与新版本新增权限是同一个状态。</summary>
    private static Task<string> RevokeFirstAsync(WebApplicationFactory<Program> host, Guid? tenantId)
        => InScopeAsync(host, tenantId, async services =>
        {
            var role = await AdminRoleAsync(services);
            var grants = await services.GetRequiredService<IPermissionGrantStore>()
                .GetGrantsAsync(PermissionGrantProviderNames.Role, role.Id.ToString());
            var name = grants.PermissionNames.Order(StringComparer.Ordinal).First();
            await services.GetRequiredService<IPermissionGrantManager>()
                .RevokeAsync(name, PermissionGrantProviderNames.Role, role.Id.ToString());
            return name;
        });

    private static Task<IReadOnlyList<string>> MissingAsync(WebApplicationFactory<Program> host, Guid? tenantId, string roleName = AdminConstant.RoleName)
        => InScopeAsync(host, tenantId, async services => await services.GetRequiredService<IPermissionGrantSeeder>().GetMissingAsync(
            PermissionGrantProviderNames.Role, (await AdminRoleAsync(services, roleName)).Id.ToString(),
            tenantId.HasValue ? MultiTenancySides.Tenant : MultiTenancySides.Host));

    private static async Task<Role> AdminRoleAsync(IServiceProvider services, string roleName = AdminConstant.RoleName)
        => (await services.GetRequiredService<IRepository<Role, Guid>>().GetFirstAsync(role => role.Name == roleName))!;

    private static async Task<T> InScopeAsync<T>(WebApplicationFactory<Program> host, Guid? tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        using (var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true))
        {
            var result = await action(scope.ServiceProvider);
            await uow.CompleteAsync();
            return result;
        }
    }

    /// <summary>宿主 Admin 的授予行数与版本；任何一次写入都会改变它。</summary>
    private static async Task<(long Grants, long Versions)> StateAsync(string connection, Guid? tenantId)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        var tenantFilter = tenantId is null ? "IS NULL" : $"= '{tenantId}'";
        command.CommandText = $"SELECT (SELECT count(*) FROM {Schema}.\"PermissionGrantRecords\" WHERE \"TenantId\" {tenantFilter}), "
            + $"(SELECT coalesce(sum(\"Version\"), 0)::bigint FROM {Schema}.\"AuthorizationVersionRecord\" WHERE \"TenantId\" {tenantFilter})";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<long> ScalarAsync(string connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(int Exit, string Output)> RunAsync(CommandHost env, params string[] arguments)
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
    private sealed class CommandHost : IDisposable
    {
        private readonly ProjectWebApplicationFactory factory = new();

        public CommandHost()
        {
            KeysPath = Path.Combine(Path.GetTempPath(), $"ProjectTests-topup-keys-{Guid.NewGuid():N}");
            Host = factory.WithWebHostBuilder(builder => builder.UseSetting("DataProtection:KeysPath", KeysPath));
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
}
#endif
