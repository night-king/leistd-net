#if (LocalIdentity)
extern alias Migrator;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Security.OneTimeCodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MigratorDependencyInjection = Migrator::CompanyName.ProjectName.DbMigrator.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>两步验证的部署级重置：真实 PostgreSQL、真实 CLI。</summary>
/// <remarks>
/// 内置超级管理员只能由本人操作，管理员又不能重置自己：验证器丢了、恢复码也用尽时，在线没有任何入口。
/// 这条命令就是那时的出口——默认只预演，<c>--apply</c> 才写入，写入后本人用口令登录、重新设置。
/// </remarks>
public sealed class TwoFactorResetCommandTests
{
    [Fact]
    public async Task The_super_administrator_who_lost_the_authenticator_is_reset_by_the_deployment_command()
    {
        using var env = new CommandHost();
        using var admin = await ProjectWebApplicationFactory.LoginAsync(env.Host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        await EnableTwoFactorAsync(admin.Client);
        var adminId = await AdminIdAsync(admin.Client);

        var dry = await RunAsync(env, adminId);
        Assert.True(dry.Exit == 0, dry.Output);
        Assert.Contains("Dry run", dry.Output);
        Assert.True(await RequiresSecondStepAsync(env.Host));
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/api/v1/auth/me")).StatusCode);

        var applied = await RunAsync(env, adminId, "--apply");
        Assert.True(applied.Exit == 0, applied.Output);
        Assert.Contains("Reset two-factor authentication", applied.Output);
        // 会话一并结束：丢了验证器的人手里不该还留着别处登录的会话。
        // 测试宿主与命令不共享缓存，API 对刚校验过的会话最多滞后一个校验间隔，所以看库里的会话行而不是立即 401
        Assert.Equal(0, await SessionCountAsync(env.Host, adminId));
        // 口令一步即可登录（这一步本身会建一个新会话，所以放在数会话之后）
        Assert.False(await RequiresSecondStepAsync(env.Host));

        var again = await RunAsync(env, adminId, "--apply");
        Assert.True(again.Exit == 0, again.Output);
        Assert.Contains("nothing to reset", again.Output);
    }

    [Fact]
    public async Task An_unknown_user_changes_nothing_and_fails()
    {
        using var env = new CommandHost();

        var result = await RunAsync(env, Guid.NewGuid(), "--apply");

        Assert.Equal(1, result.Exit);
        Assert.Contains("does not exist", result.Output);
    }

    private static async Task EnableTwoFactorAsync(HttpClient client)
    {
        using var begin = await client.PostAsJsonAsync("/api/v1/auth/me/two-factor/setup", new { });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using var setup = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var secret = Totp.ParseSecret(setup.RootElement.GetProperty("secret").GetString()!)!;
        using var enable = await client.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable", new
        {
            SetupId = setup.RootElement.GetProperty("setupId").GetGuid(),
            Code = Totp.ComputeCode(secret, Totp.TimeStepAt(DateTimeOffset.UtcNow))
        });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
    }

    private static async Task<Guid> AdminIdAsync(HttpClient admin)
    {
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users?offset=0&limit=10&keyword=admin");
        return users.GetProperty("items").EnumerateArray()
            .Single(user => user.GetProperty("username").GetString() == "admin")
            .GetProperty("id").GetGuid();
    }

    private static async Task<int> SessionCountAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>()
            .Set<UserSession>().CountAsync(session => session.UserId == userId);
    }

    /// <summary>口令这一步之后是否还要第二步。</summary>
    private static async Task<bool> RequiresSecondStepAsync(WebApplicationFactory<Program> host)
    {
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login", new { UsernameOrEmail = "admin", Password = ProjectWebApplicationFactory.TestAdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("requiresTwoFactor").GetBoolean();
    }

    private static async Task<(int Exit, string Output)> RunAsync(CommandHost env, Guid userId, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CompanyName.ProjectName.IntegrationTests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CompanyName.ProjectName.IntegrationTests.deps.json"));
        start.ArgumentList.Add(typeof(MigratorDependencyInjection).Assembly.Location);
        start.ArgumentList.Add("--reset-two-factor");
        start.ArgumentList.Add(userId.ToString());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["ConnectionStrings__Default"] = env.Connection;
        start.Environment["ConnectionStrings__Redis"] = "";
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
            KeysPath = Path.Combine(Path.GetTempPath(), $"ProjectTests-2fa-reset-keys-{Guid.NewGuid():N}");
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
