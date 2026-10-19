#if (LocalIdentity)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Security.OneTimeCodes;
using Leistd.OperationRecords.Recording;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
#if (OpenIddictServer)
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.UnitOfWork;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using static OpenIddict.Abstractions.OpenIddictConstants;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 凭据与访问变更的事务边界：中途失败时状态、会话撤销与成功审计整体回滚，也不发提醒；
/// 成功时撤销照旧生效，每种提醒只发一次。
/// </summary>
/// <remarks>
/// 失败注入在用例写完用户行、撤完会话之后的那一步：本人用例注入在成功审计，管理员用例注入在最后的令牌撤销
/// （没有令牌签发时注入在成功审计）。断言落在"另一台设备的 Cookie 还能不能用""旧口令还能不能登录"上。
/// </remarks>
public sealed class CredentialChangeTransactionTests(CredentialChangeTransactionTests.Host fixture)
    : IClassFixture<CredentialChangeTransactionTests.Host>
{
    private const string Password = "TxTests!Passw0rd";

    [Fact]
    public async Task Failed_password_change_keeps_old_password_and_other_sessions_and_sends_no_alert()
    {
        var (username, userId) = await CreateUserAsync("tx_pwd");
        using var current = await LoginAsync(username, Password);
        using var other = await LoginAsync(username, Password);

        fixture.Faults.FailRecordingOf = OperationRecordActions.AuthPasswordChanged;
        try
        {
            using var failed = await ChangePasswordAsync(current.Client, Password + "1");
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally
        {
            fixture.Faults.Clear();
        }

        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        using (await LoginAsync(username, Password))
        {
        }

        Assert.Empty(fixture.Alerts.For(userId));
        Assert.Equal(0, await CountSucceededAsync(OperationRecordActions.AuthPasswordChanged, userId));

        using (var changed = await ChangePasswordAsync(current.Client, Password + "1"))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal([SecurityAlertKind.PasswordChanged], fixture.Alerts.For(userId));
        Assert.Equal(1, await CountSucceededAsync(OperationRecordActions.AuthPasswordChanged, userId));
    }

    /// <summary>启用回滚时设置密钥还在，同一个验证器不必重新扫码；提交后密钥才删除。</summary>
    [Fact]
    public async Task Rolled_back_two_factor_enable_keeps_the_setup_secret_and_commit_clears_it()
    {
        var (username, userId) = await CreateUserAsync("tx_tfa_on");
        using var current = await LoginAsync(username, Password);
        using var other = await LoginAsync(username, Password);
        var setup = await BeginSetupAsync(current.Client);
        var secret = setup.Secret;

        fixture.Faults.FailRecordingOf = OperationRecordActions.AuthTwoFactorEnabled;
        try
        {
            using var failed = await EnableAsync(current.Client, setup);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally
        {
            fixture.Faults.Clear();
        }

        Assert.False(await IsTwoFactorEnabledAsync(current.Client));
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.NotNull(await SetupSecretAsync(userId));
        Assert.Empty(fixture.Alerts.For(userId));
        Assert.Equal(0, await CountSucceededAsync(OperationRecordActions.AuthTwoFactorEnabled, userId));

        // 不重新开始设置：回滚保留下来的那份密钥直接确认
        fixture.Step();
        using (var enabled = await EnableAsync(current.Client, setup))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        Assert.True(await IsTwoFactorEnabledAsync(current.Client));
        Assert.Null(await SetupSecretAsync(userId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal([SecurityAlertKind.TwoFactorEnabled], fixture.Alerts.For(userId));
        Assert.Equal(1, await CountSucceededAsync(OperationRecordActions.AuthTwoFactorEnabled, userId));
    }

    [Fact]
    public async Task Failed_two_factor_disable_keeps_it_enabled_and_sends_no_alert()
    {
        var (username, userId) = await CreateUserAsync("tx_tfa_off");
        using var current = await LoginAsync(username, Password);
        var setup = await BeginSetupAsync(current.Client);
        var secret = setup.Secret;
        using (var enabled = await EnableAsync(current.Client, setup))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        fixture.Alerts.Clear(userId);

        fixture.Step();
        fixture.Faults.FailRecordingOf = OperationRecordActions.AuthTwoFactorDisabled;
        try
        {
            using var failed = await DisableTwoFactorAsync(current.Client, secret);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally
        {
            fixture.Faults.Clear();
        }

        Assert.True(await IsTwoFactorEnabledAsync(current.Client));
        Assert.Empty(fixture.Alerts.For(userId));

        // 失败的那次没有记下验证码所在的步，同一步的码仍可再用一次；换一步只为不依赖这一点
        fixture.Step();
        using (var disabled = await DisableTwoFactorAsync(current.Client, secret))
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        Assert.False(await IsTwoFactorEnabledAsync(current.Client));
        Assert.Equal([SecurityAlertKind.TwoFactorDisabled], fixture.Alerts.For(userId));
        Assert.Equal(1, await CountSucceededAsync(OperationRecordActions.AuthTwoFactorDisabled, userId));
    }

    /// <summary>管理员的停用、重置口令、重置两步验证与删除：最后一步失败时整体回滚，成功时撤销会话（与已签发令牌）并只发一次提醒。</summary>
    [Theory]
    [InlineData("disable")]
    [InlineData("reset-password")]
    [InlineData("reset-two-factor")]
    [InlineData("delete")]
    public async Task Admin_access_change_rolls_back_as_a_whole_when_its_last_step_fails(string operation)
    {
        var (username, userId) = await CreateUserAsync("tx_admin");
        using var target = await LoginAsync(username, Password);
        if (operation == "reset-two-factor")
        {
            var setup = await BeginSetupAsync(target.Client);
            var secret = setup.Secret;
            using var enabled = await EnableAsync(target.Client, setup);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            fixture.Alerts.Clear(userId);
        }
#if (OpenIddictServer)
        var tokenId = await IssueAccessTokenEntryAsync(userId);
#endif
        using var admin = await LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var (action, alert) = operation switch
        {
            "disable" => (OperationRecordActions.UserDisabled, (SecurityAlertKind?)null),
            "reset-password" => (OperationRecordActions.UserPasswordReset, SecurityAlertKind.PasswordReset),
            "reset-two-factor" => (OperationRecordActions.UserTwoFactorReset, SecurityAlertKind.TwoFactorReset),
            _ => (OperationRecordActions.UserDeleted, (SecurityAlertKind?)null)
        };

#if (OpenIddictServer)
        fixture.Faults.FailTokenRevocation = true;
#else
        fixture.Faults.FailRecordingOf = action;
#endif
        try
        {
            using var failed = await PerformAsync(admin.Client, operation, userId);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally
        {
            fixture.Faults.Clear();
        }

        var user = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users/{userId}");
        Assert.True(user.GetProperty("isActive").GetBoolean());
        Assert.Equal(operation == "reset-two-factor", user.GetProperty("isTwoFactorEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await target.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        if (operation == "reset-password")
        {
            using (await LoginAsync(username, Password))
            {
            }
        }

        Assert.Empty(fixture.Alerts.For(userId));
        Assert.Equal(0, await CountSucceededAsync(action, userId, admin.Client));
#if (OpenIddictServer)
        Assert.Equal(Statuses.Valid, await TokenStatusAsync(tokenId));
#endif

        using (var succeeded = await PerformAsync(admin.Client, operation, userId))
            Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await target.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        SecurityAlertKind[] expectedAlerts = alert is { } kind ? [kind] : [];
        Assert.Equal(expectedAlerts, fixture.Alerts.For(userId));
        Assert.Equal(1, await CountSucceededAsync(action, userId, admin.Client));
#if (OpenIddictServer)
        Assert.Equal(Statuses.Revoked, await TokenStatusAsync(tokenId));
#endif
    }

    /// <summary>再认证失败触发锁定时，改口令的工作单元随拒绝回滚，锁定提醒仍要送达本人。</summary>
    [Fact]
    public async Task Lockout_during_password_change_still_alerts_the_user()
    {
        var (username, userId) = await CreateUserAsync("tx_lock");
        using var session = await LoginAsync(username, Password);

        for (var i = 0; i < SettingConstant.Security.DefaultLockoutMaxFailedAttempts; i++)
        {
            using var rejected = await ChangePasswordAsync(session.Client, Password + "1", current: "Wrong!Passw0rd");
            Assert.False(rejected.IsSuccessStatusCode);
        }

        Assert.Equal([SecurityAlertKind.LockedOut], fixture.Alerts.For(userId));
#if (IncludeNotifications)
        // 锁定只挡新登录，已有会话照常可读通知；提醒写入若跟着改口令回滚，这里就是空的
        using var body = JsonDocument.Parse(await session.Client.GetStringAsync("/api/v1/notifications"));
        Assert.Single(body.RootElement.EnumerateArray(), n => n.GetProperty("type").GetString() == "Security");
#endif
    }
#if (OpenIddictServer)

    /// <summary>
    /// 令牌撤销不进业务事务：OpenIddict 存储用自己的上下文与连接，工作单元回滚撤不回它。
    /// <c>UserAppService.RevokeAllAccessAsync</c> 因此放在提交前的最后一步，而不是指望回滚。
    /// </summary>
    [Fact]
    public async Task Token_revocation_takes_effect_even_when_the_business_unit_of_work_rolls_back()
    {
        var (_, userId) = await CreateUserAsync("tx_probe");
        var tokenId = await IssueAccessTokenEntryAsync(userId);

        await using (var scope = fixture.Server.Services.CreateAsyncScope())
        {
            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var users = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            using var unitOfWork = unitOfWorkManager.Begin();
            var user = (await users.GetByIdAsync(userId))!;
            user.Disable();
            await users.UpdateAsync(user);
            await unitOfWork.SaveChangesAsync();

            await tokens.RevokeBySubjectAsync(userId.ToString());
            await unitOfWork.RollbackAsync();
        }

        using var admin = await LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var stored = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users/{userId}");
        Assert.True(stored.GetProperty("isActive").GetBoolean());
        Assert.Equal(Statuses.Revoked, await TokenStatusAsync(tokenId));
    }

    private async Task<string> IssueAccessTokenEntryAsync(Guid userId)
    {
        await using var scope = fixture.Server.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var now = DateTimeOffset.UtcNow;
        var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            Subject = userId.ToString(), Type = TokenTypeHints.AccessToken, Status = Statuses.Valid,
            CreationDate = now, ExpirationDate = now.AddHours(1)
        });
        return (await tokens.GetIdAsync(token))!;
    }

    private async Task<string?> TokenStatusAsync(string tokenId)
    {
        await using var scope = fixture.Server.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        return await tokens.GetStatusAsync((await tokens.FindByIdAsync(tokenId))!);
    }
#endif

    private static Task<HttpResponseMessage> PerformAsync(HttpClient admin, string operation, Guid userId) => operation switch
    {
        "disable" => admin.PatchAsync($"/api/v1/users/{userId}/disable", null),
        "reset-password" => admin.PostAsJsonAsync($"/api/v1/users/{userId}/reset-password", new { Password = Password + "R" }),
        "reset-two-factor" => admin.PostAsJsonAsync($"/api/v1/users/{userId}/reset-two-factor", new { }),
        _ => admin.DeleteAsync($"/api/v1/users/{userId}")
    };

    private Task<AuthenticatedSession> LoginAsync(string username, string password) =>
        ProjectWebApplicationFactory.LoginAsync(fixture.Server, username, password);

    private async Task<int> CountSucceededAsync(string action, Guid userId, HttpClient? reader = null)
    {
        if (reader is not null)
            return await OperationRecordQueries.CountSucceededAsync(fixture.Server, reader, action, userId.ToString());

        using var admin = await LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        return await OperationRecordQueries.CountSucceededAsync(fixture.Server, admin.Client, action, userId.ToString());
    }

    private async Task<string?> SetupSecretAsync(Guid userId) =>
        await fixture.Server.Services.GetRequiredService<IDistributedCache>().GetStringAsync(TwoFactorAppService.SetupKey(userId));

    private static Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string next, string current = Password) =>
        client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            CurrentPassword = current,
            NewPassword = next,
            ConfirmPassword = next
        });

    private static async Task<(Guid SetupId, string Secret)> BeginSetupAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/me/two-factor/setup", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("setupId").GetGuid(), body.RootElement.GetProperty("secret").GetString()!);
    }

    private Task<HttpResponseMessage> EnableAsync(HttpClient client, (Guid SetupId, string Secret) setup) =>
        client.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable", new { setup.SetupId, Code = fixture.Code(setup.Secret) });

    private Task<HttpResponseMessage> DisableTwoFactorAsync(HttpClient client, string secret) =>
        client.PostAsJsonAsync("/api/v1/auth/me/two-factor/disable", new { Password, Code = fixture.Code(secret) });

    private static async Task<bool> IsTwoFactorEnabledAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me/two-factor")).GetProperty("enabled").GetBoolean();

    private async Task<(string Username, Guid UserId)> CreateUserAsync(string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var admin = await LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return (username, body.GetProperty("id").GetGuid());
    }

    /// <summary>本类共用的派生宿主：可开关的失败注入、记下发出的安全提醒、手动推进的时钟。</summary>
    public sealed class Host : IDisposable
    {
        private readonly ProjectWebApplicationFactory factory = new();

        public Host()
        {
            Server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                // 从此刻起步：缓存条目按真实时钟回收，假时钟落在过去会让刚写入的设置密钥被当成已过期
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);

                Decorate<IOperationRecorder>(services, (inner, _) => FaultProxy<IOperationRecorder>.Create(
                    inner,
                    (method, args) => method.Name == nameof(IOperationRecorder.RecordSucceededAsync)
                        && Faults.FailRecordingOf is { } action
                        && Equals(args?[0], action)));
                Decorate<ISecurityAlertPublisher>(services, (inner, _) => new RecordingSecurityAlertPublisher(inner, Alerts));
#if (OpenIddictServer)
                Decorate<IOpenIddictTokenManager>(services, (inner, _) => FaultProxy<IOpenIddictTokenManager>.Create(
                    inner,
                    (method, _) => method.Name == nameof(IOpenIddictTokenManager.RevokeBySubjectAsync)
                        && Faults.FailTokenRevocation));
                // 读令牌状态要看库里的真实值，不看作用域内的实体缓存
                services.Configure<OpenIddictCoreOptions>(options => options.DisableEntityCaching = true);
#endif
            }));
        }

        public WebApplicationFactory<Program> Server { get; }

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public InjectedFaults Faults { get; } = new();

        public SecurityAlertLog Alerts { get; } = new();

        /// <summary>推进到下一个验证码时间步：同一步的码只认一次。</summary>
        public void Step() => Clock.Advance(TimeSpan.FromSeconds(30));

        public string Code(string secret) =>
            Totp.ComputeCode(Totp.ParseSecret(secret)!, Totp.TimeStepAt(Clock.GetUtcNow()));

        public void Dispose()
        {
            Server.Dispose();
            factory.Dispose();
        }

        private static void Decorate<TService>(IServiceCollection services, Func<TService, IServiceProvider, TService> decorate)
            where TService : class
        {
            var descriptor = services.Last(d => d.ServiceType == typeof(TService));
            services.Remove(descriptor);
            services.Add(ServiceDescriptor.Describe(
                typeof(TService),
                provider => decorate((TService)CreateInner(provider, descriptor), provider),
                descriptor.Lifetime));
        }

        private static object CreateInner(IServiceProvider provider, ServiceDescriptor descriptor) =>
            descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    /// <summary>失败注入开关。同一测试类的用例顺序执行，开关在用例内设置并在 finally 里清除。</summary>
    public sealed class InjectedFaults
    {
        public volatile string? FailRecordingOf;
        public volatile bool FailTokenRevocation;

        public void Clear()
        {
            FailRecordingOf = null;
            FailTokenRevocation = false;
        }
    }

    /// <summary>按用户记下真正交给发布方的提醒种类。</summary>
    public sealed class SecurityAlertLog
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<SecurityAlertKind>> alerts = new();

        public void Add(Guid userId, SecurityAlertKind kind) => alerts.GetOrAdd(userId, _ => new()).Enqueue(kind);

        public SecurityAlertKind[] For(Guid userId) => alerts.TryGetValue(userId, out var kinds) ? kinds.ToArray() : [];

        public void Clear(Guid userId) => alerts.TryRemove(userId, out _);
    }

    private sealed class RecordingSecurityAlertPublisher(ISecurityAlertPublisher inner, SecurityAlertLog log) : ISecurityAlertPublisher
    {
        public Task PublishAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken = default)
        {
            log.Add(userId, alert.Kind);
            return inner.PublishAsync(userId, alert, cancellationToken);
        }
    }

    /// <summary>转发到原实现，命中条件的调用改为抛出。</summary>
    public class FaultProxy<TService> : DispatchProxy where TService : class
    {
        private TService inner = null!;
        private Func<MethodInfo, object?[]?, bool> shouldFail = null!;

        public static TService Create(TService inner, Func<MethodInfo, object?[]?, bool> shouldFail)
        {
            var proxy = Create<TService, FaultProxy<TService>>();
            var state = (FaultProxy<TService>)(object)proxy;
            state.inner = inner;
            state.shouldFail = shouldFail;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (shouldFail(targetMethod, args))
                throw new InvalidOperationException($"Injected failure in {typeof(TService).Name}.{targetMethod.Name}.");

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        }
    }
}
#endif
