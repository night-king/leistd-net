#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 口令登录与改口令、管理员重置口令交错：撤权之后不能再冒出一个凭旧口令建立的会话。
/// </summary>
/// <remarks>
/// 交错用门控替身固定：登录停在登记会话之后、提交之前（登录成功记录的写入处），或取凭据锁之前，
/// 另一边改口令或重置口令；放行后看登录得到的会话还能不能用。
/// </remarks>
public sealed class CredentialConcurrencyTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "Concurrency!Passw0rd";
    private const string NewPassword = "Concurrency!Passw0rd2";

    // 登录停在登记会话之后、提交之前，持着凭据锁：改口令要等它提交完再撤，撤掉的就包括这一个
    [Fact]
    public async Task A_sign_in_racing_a_password_change_does_not_outlive_the_change()
    {
        var gate = new InterleavingGate();
        var waits = new LockWaitProbe();
        using var host = CreateGatedHost(gate, waits);
        var (username, userId) = await CreateUserAsync(host, "race_pwd");
        using var current = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        gate.Arm();
        var signIn = SignInAsync(host, username, Password);
        await gate.Entered;
        waits.Arm(UserCredentialLock.Key(userId));
        var change = current.Client.PostAsJsonAsync(
            "/api/v1/auth/change-password", new { CurrentPassword = Password, NewPassword, ConfirmPassword = NewPassword });
        await waits.AssertWaitingAsync(change);
        gate.Release();

        using var raced = await signIn;
        using (var changed = await change)
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raced.GetAsync("/api/v1/auth/me")).StatusCode);
        using (await ProjectWebApplicationFactory.LoginAsync(host, username, NewPassword))
        {
        }
    }

    // 管理员重置口令同理：重置之后，交错中凭旧口令建立的会话不能留下
    [Fact]
    public async Task A_sign_in_racing_an_admin_password_reset_does_not_outlive_the_reset()
    {
        var gate = new InterleavingGate();
        var waits = new LockWaitProbe();
        using var host = CreateGatedHost(gate, waits);
        var (username, userId) = await CreateUserAsync(host, "race_reset");
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        gate.Arm();
        var signIn = SignInAsync(host, username, Password);
        await gate.Entered;
        waits.Arm(UserCredentialLock.Key(userId));
        var reset = admin.Client.PostAsJsonAsync(
            $"/api/v1/users/{userId}/reset-password",
            new { Password = NewPassword, CurrentPassword = ProjectWebApplicationFactory.TestAdminPassword });
        await waits.AssertWaitingAsync(reset);
        gate.Release();

        using var raced = await signIn;
        using (var done = await reset)
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raced.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // 口令已校验、还没取到凭据锁时口令被改：锁内复核安全版本，这次登录按口令不正确拒绝
    [Fact]
    public async Task A_sign_in_whose_password_changed_before_it_took_the_lock_is_rejected()
    {
        var gate = new InterleavingGate();
        Guid userId = default;
        using var host = CreateHost(services => gate.GateLock(services, key => key.EndsWith(userId.ToString("N"), StringComparison.Ordinal)));
        (var username, userId) = await CreateUserAsync(host, "race_stamp");
        using var current = await ProjectWebApplicationFactory.LoginAsync(host, username, Password);

        gate.Arm();
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        var signIn = client.PostAsJsonAsync("/api/v1/auth/session-login", new { UsernameOrEmail = username, Password });
        await gate.Entered;
        using (var changed = await current.Client.PostAsJsonAsync(
                   "/api/v1/auth/change-password", new { CurrentPassword = Password, NewPassword, ConfirmPassword = NewPassword }))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        gate.Release();

        using var rejected = await signIn;
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("Auth:InvalidCredentials", await ErrorCodeAsync(rejected));
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
    }

    private WebApplicationFactory<Program> CreateHost(Action<IServiceCollection> configure) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(configure));

    // 登录停在成功记录处（持着凭据锁）；另一边在同一把锁上开始等待时发出信号
    private WebApplicationFactory<Program> CreateGatedHost(InterleavingGate gate, LockWaitProbe waits) => CreateHost(services =>
    {
        InterleavingGate.Wrap<IOperationRecorder>(services, inner => new LoginRecorderGate(gate, inner));
        waits.Install(services);
    });

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, string username, string password)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/session-login", new { UsernameOrEmail = username, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0])));
        return client;
    }

    private static async Task<(string Username, Guid Id)> CreateUserAsync(WebApplicationFactory<Program> host, string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var admin = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return (username, body.RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // 只拦登录成功的记录：它在会话登记之后、工作单元提交之前写入，此时登录持着凭据锁
    private sealed class LoginRecorderGate(InterleavingGate gate, IOperationRecorder inner) : IOperationRecorder
    {
        public async Task RecordSucceededAsync(
            string action, OperationTarget target, string authorizationBasis, CancellationToken cancellationToken = default)
        {
            if (action == OperationRecordActions.AuthLoginSucceeded)
                await gate.PassAsync();
            await inner.RecordSucceededAsync(action, target, authorizationBasis, cancellationToken);
        }

        public Task RecordFailedAsync(
            string action, OperationTarget target, string authorizationBasis, OperationFailure failure = default)
            => inner.RecordFailedAsync(action, target, authorizationBasis, failure);
    }
}
#endif
