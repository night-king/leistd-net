#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Ddd.Domain.Repositories;
using Leistd.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>再认证失败与登录共用同一套失败计数与锁定。</summary>
/// <remarks>
/// 改口令前先检查锁定，再校验当前口令；失败共用登录计数器并留下审计。
/// 临时锁定不撤销已有会话，因此再认证入口必须自行拒绝锁定账号。
/// </remarks>
public sealed class ReauthenticationLockoutTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "ReauthTests!Passw0rd";
    private const string WrongPassword = "ReauthTests!Wrong000";
    private const string NewPassword = "ReauthTests!NewPassw0rd";
    private static readonly int Threshold = SettingConstant.Security.DefaultLockoutMaxFailedAttempts;

    /// <summary>猜错当前口令会计数，到阈值即锁定——而不是无限次可试。</summary>
    [Fact]
    public async Task Wrong_current_password_counts_toward_the_lockout_threshold()
    {
        var username = await CreateUserAsync("reauth_count");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        for (var i = 1; i < Threshold; i++)
        {
            Assert.Equal("Security:CurrentPasswordIncorrect", await ChangePasswordErrorAsync(session.Client, WrongPassword));
        }

        // 触发锁定的那一次改说"已锁定"，不再是"口令不正确"
        Assert.Equal("Auth:UserTemporarilyLockedOut", await ChangePasswordErrorAsync(session.Client, WrongPassword));
    }

    /// <summary>锁定期内不再校验口令：正确的当前口令也一样被拒，试不出哪个是对的。</summary>
    [Fact]
    public async Task The_correct_current_password_is_refused_while_locked_out()
    {
        var username = await CreateUserAsync("reauth_locked");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        for (var i = 0; i < Threshold; i++)
        {
            await ChangePasswordErrorAsync(session.Client, WrongPassword);
        }

        Assert.Equal("Auth:UserTemporarilyLockedOut", await ChangePasswordErrorAsync(session.Client, Password));
    }

    /// <summary>在改口令接口上把自己锁了，登录页也进不去——两边是同一个计数器。</summary>
    [Fact]
    public async Task A_lockout_earned_here_also_blocks_a_fresh_sign_in()
    {
        var username = await CreateUserAsync("reauth_shared");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        for (var i = 0; i < Threshold; i++)
        {
            await ChangePasswordErrorAsync(session.Client, WrongPassword);
        }

        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = username, Password });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("Auth:UserTemporarilyLockedOut", await ErrorCodeAsync(login));
    }

    /// <summary>再认证失败留下审计。</summary>
    [Fact]
    public async Task A_failed_attempt_is_recorded_for_the_audit_trail()
    {
        var username = await CreateUserAsync("reauth_audit");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        await ChangePasswordErrorAsync(session.Client, WrongPassword);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
#if (IncludeOperationRecords)
        using var body = JsonDocument.Parse(await admin.Client.GetStringAsync(
            "/api/v1/operation-records?offset=0&limit=50&actions=auth.password.changed&outcome=failed"));

        var failureCodes = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("failureCode").GetString())
            .ToList();
#else
        var failureCodes = factory.Services.GetRequiredService<OperationRecordLogCapture>().Snapshot()
            .Where(entry => Equals(OperationRecordLogCapture.Field(entry, "OperationAction"), "auth.password.changed"))
            .Select(entry => OperationRecordLogCapture.Field(entry, "OperationFailureCode") as string);
#endif
        Assert.Contains("Security:CurrentPasswordIncorrect", failureCodes);
    }

    /// <summary>临时锁定不撤销已有会话。</summary>
    [Fact]
    public async Task The_current_session_keeps_working_while_locked_out()
    {
        var username = await CreateUserAsync("reauth_session");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        for (var i = 0; i < Threshold; i++)
        {
            await ChangePasswordErrorAsync(session.Client, WrongPassword);
        }

        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>口令正确时照常改成功，计数不残留。</summary>
    [Fact]
    public async Task A_successful_change_still_works_after_some_failures()
    {
        var username = await CreateUserAsync("reauth_success");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, username, Password);

        await ChangePasswordErrorAsync(session.Client, WrongPassword);

        using var changed = await session.Client.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new { CurrentPassword = Password, NewPassword, ConfirmPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        using var signedIn = await ProjectWebApplicationFactory.LoginAsync(factory, username, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    /// <summary>守卫经计数器累计，因而计数不随调用方的事务回滚。</summary>
    /// <remarks>
    /// 调用守卫后丢弃外层工作单元，失败计数仍须落库；验证守卫使用独立提交的计数器。
    /// <c>AccessFailureCounterTests</c> 单独验证计数器自身的提交独立性。
    /// 登录与两步验证路径尚无外层工作单元回滚判据。
    /// </remarks>
    [Fact]
    public async Task The_guard_counts_through_the_shared_counter()
    {
        var username = await CreateUserAsync("reauth_uow");
        var userId = await FindUserIdAsync(username);

        using (var scope = factory.Services.CreateScope())
        {
            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var guard = scope.ServiceProvider.GetRequiredService<IReauthenticationGuard>();
            var users = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();

            using var unitOfWork = unitOfWorkManager.Begin();
            var user = await users.GetByIdAsync(userId);
            Assert.NotNull(user);

            await guard.RejectAsync(
                user,
                OperationRecordActions.AuthPasswordChanged,
                SecurityErrorCodes.CurrentPasswordIncorrect,
                "The current password is incorrect.");

            // 不 CompleteAsync：调用方这一侧的写入在这里全部丢弃
        }

        Assert.Equal(1, await AccessFailedCountAsync(userId));
    }

    private async Task<Guid> FindUserIdAsync(string username)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return (await db.Set<User>().IgnoreQueryFilters()
            .SingleAsync(user => user.Username == username)).Id;
    }

    private async Task<int> AccessFailedCountAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return (await db.Set<User>().IgnoreQueryFilters()
            .SingleAsync(user => user.Id == userId)).Lockout.AccessFailedCount;
    }

    private static async Task<string?> ChangePasswordErrorAsync(HttpClient client, string currentPassword)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new { CurrentPassword = currentPassword, NewPassword, ConfirmPassword = NewPassword });
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        return await ErrorCodeAsync(response);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<string> CreateUserAsync(string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return username;
    }
}
#endif
