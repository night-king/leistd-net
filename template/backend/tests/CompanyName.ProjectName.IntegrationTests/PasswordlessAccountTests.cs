#if (ExternalLogin)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Security.OneTimeCodes;
using Leistd.UnitOfWork;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 只经外部登录建立、没有本地口令的账号：自助设置第一个口令，只凭验证码停用两步验证，
/// 以及丢了验证器、会话又开久了时靠重新登录（第二步用恢复码）走回换绑。
/// </summary>
/// <remarks>
/// 账号经真实的外部登录处理器建立（只替换提供商的后端通道）。时钟换成手动推进的：会话要开过"刚认证过"的窗口才会要求凭证，
/// 同一步的验证码只认一次。
/// </remarks>
public sealed class PasswordlessAccountTests
{
    private const string StateCookieName = "__Host-CompanyName.ProjectName.External";
    private const string NewPassword = "Passwordless!Passw0rd";

    /// <summary>超出"刚认证过"的窗口。</summary>
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromMinutes(16);

    // 刚完成外部登录的会话直接设置口令：保留当前会话、撤销其他会话、轮换安全版本、发"口令已修改"提醒，之后能用口令登录
    [Fact]
    public async Task A_fresh_external_session_sets_the_first_password_and_signs_out_other_sessions()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-set-pwd");
        using var current = await context.SignInExternallyAsync();
        using var other = await context.SignInExternallyAsync();
        var me = await context.MeAsync(current);
        Assert.False(me.GetProperty("hasPassword").GetBoolean());
        var userId = me.GetProperty("id").GetGuid();
        var stamp = (await context.ReadUserAsync(userId)).SecurityStamp;

        using (var set = await SetPasswordAsync(current))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        Assert.True((await context.MeAsync(current)).GetProperty("hasPassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.NotEqual(stamp, (await context.ReadUserAsync(userId)).SecurityStamp);
        Assert.Equal([SecurityAlertKind.PasswordChanged], context.Alerts.For(userId));
        using (await ProjectWebApplicationFactory.LoginAsync(context.Host, me.GetProperty("username").GetString()!, NewPassword))
        {
        }
    }

    // 已有口令就冲突：不改口令、不轮换版本、不发提醒；两个请求同时设置只有一个成功
    [Fact]
    public async Task Only_one_first_password_can_be_set_and_a_second_attempt_changes_nothing()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-set-twice");
        using var session = await context.SignInExternallyAsync();
        var userId = (await context.MeAsync(session)).GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(SetPasswordAsync(session), SetPasswordAsync(session, NewPassword + "2"));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal("Security:PasswordAlreadySet", await ErrorCodeAsync(responses.Single(response => response.StatusCode != HttpStatusCode.OK)));
            Assert.Equal(HttpStatusCode.Conflict, responses.Single(response => response.StatusCode != HttpStatusCode.OK).StatusCode);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }

        var stamp = (await context.ReadUserAsync(userId)).SecurityStamp;
        using (var again = await SetPasswordAsync(session, NewPassword + "3"))
            Assert.Equal("Security:PasswordAlreadySet", await ErrorCodeAsync(again));
        Assert.Equal(stamp, (await context.ReadUserAsync(userId)).SecurityStamp);
        Assert.Equal([SecurityAlertKind.PasswordChanged], context.Alerts.For(userId));
    }

    // 开久了、既没有口令也没开两步验证：拿不出任何凭证，只能重新登录；重新登录得到新的认证时间后照常设置
    [Fact]
    public async Task An_older_session_without_any_proof_must_sign_in_again_to_set_a_password()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-set-stale");
        using var stale = await context.SignInExternallyAsync();
        context.Clock.Advance(StaleSessionAge);

        using (var rejected = await SetPasswordAsync(stale))
            Assert.Equal("Auth:SignInAgainRequired", await ErrorCodeAsync(rejected));
        Assert.False((await context.MeAsync(stale)).GetProperty("hasPassword").GetBoolean());

        using var again = await context.SignInExternallyAsync();
        using var set = await SetPasswordAsync(again);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    // 开久了但开着两步验证：要的是验证码（不是重新登录），给了验证码就能设置
    [Fact]
    public async Task An_older_session_with_two_factor_confirms_the_first_password_with_a_code()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-set-code");
        using var session = await context.SignInExternallyAsync();
        var (secret, _) = await context.EnableTwoFactorAsync(session);
        context.Clock.Advance(StaleSessionAge);

        using (var bare = await SetPasswordAsync(session))
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(bare));
        using var set = await SetPasswordAsync(session, twoFactorCode: context.Code(secret));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    // 设置口令轮换安全版本：此前已过第一步、还没完成第二步的登录随之作废
    [Fact]
    public async Task Setting_the_first_password_voids_a_pending_second_step()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-set-challenge");
        using var session = await context.SignInExternallyAsync();
        var (secret, _) = await context.EnableTwoFactorAsync(session);
        var token = await context.FirstStepExternallyAsync();

        using (var set = await SetPasswordAsync(session))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        context.Step();
        using var client = ProjectWebApplicationFactory.CreateProjectClient(context.Host);
        using var second = await client.PostAsJsonAsync("/api/v1/auth/two-factor", new { Token = token, Code = context.Code(secret) });
        Assert.Equal("Auth:TwoFactorChallengeExpired", await ErrorCodeAsync(second));
    }

    // 没有口令的账号停用两步验证只要验证码；给口令是"这种方式不具备"，不计入失败
    [Fact]
    public async Task Disabling_two_factor_without_a_password_needs_only_the_code()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-disable");
        using var session = await context.SignInExternallyAsync();
        var userId = (await context.MeAsync(session)).GetProperty("id").GetGuid();
        var (secret, _) = await context.EnableTwoFactorAsync(session);
        context.Step();

        using (var withPassword = await session.PostAsJsonAsync(DisableUrl, new { Password = "anything", Code = context.Code(secret) }))
            Assert.Equal("Auth:ReauthenticationMethodUnavailable", await ErrorCodeAsync(withPassword));
        Assert.Equal(0, (await context.ReadUserAsync(userId)).Lockout.AccessFailedCount);

        using var disabled = await session.PostAsJsonAsync(DisableUrl, new { Code = context.Code(secret) });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.False((await context.ReadUserAsync(userId)).TwoFactorEnabled);
    }

    /// <summary>
    /// 丢了验证器、会话又开久了的纯外部用户：恢复码不是通用的确认身份凭证，换绑先被要求确认身份（被拒不耗恢复码）；
    /// 重新走外部登录、第二步用掉一个恢复码得到新会话，回到换绑再用掉另一个恢复码完成换绑。
    /// </summary>
    [Fact]
    public async Task A_passwordless_user_who_lost_the_authenticator_recovers_by_signing_in_again()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-recover");
        using var stale = await context.SignInExternallyAsync();
        var userId = (await context.MeAsync(stale)).GetProperty("id").GetGuid();
        var (_, recoveryCodes) = await context.EnableTwoFactorAsync(stale);
        context.Clock.Advance(StaleSessionAge);

        using (var rejected = await stale.PostAsJsonAsync(SetupUrl, new { RecoveryCode = recoveryCodes[0] }))
            Assert.Equal("Auth:ReauthenticationRequired", await ErrorCodeAsync(rejected));
        Assert.Equal(recoveryCodes.Count, (await context.ReadUserAsync(userId)).RecoveryCodesLeft);

        var token = await context.FirstStepExternallyAsync();
        using var recovered = await context.SecondStepAsync(new { Token = token, RecoveryCode = recoveryCodes[0] });
        Assert.Equal(recoveryCodes.Count - 1, (await context.ReadUserAsync(userId)).RecoveryCodesLeft);

        using var begin = await recovered.PostAsJsonAsync(SetupUrl, new { RecoveryCode = recoveryCodes[1] });
        Assert.True(begin.StatusCode == HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        using var setup = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var newSecret = setup.RootElement.GetProperty("secret").GetString()!;
        context.Step();
        using var enable = await recovered.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable",
            new { SetupId = setup.RootElement.GetProperty("setupId").GetGuid(), Code = context.Code(newSecret) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(RecoveryCodes.Count, (await context.ReadUserAsync(userId)).RecoveryCodesLeft);
    }

    // 只剩最后一个恢复码：登录第二步用掉它之后没有可出示的第二因素，换绑被拒、两步验证保持启用（需要管理员重置）
    [Fact]
    public async Task With_one_recovery_code_left_a_passwordless_user_cannot_replace_after_signing_in()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-last-code");
        using var session = await context.SignInExternallyAsync();
        var userId = (await context.MeAsync(session)).GetProperty("id").GetGuid();
        var (_, recoveryCodes) = await context.EnableTwoFactorAsync(session);
        var last = recoveryCodes[^1];
        await context.KeepOnlyRecoveryCodeAsync(userId, last);
        context.Clock.Advance(StaleSessionAge);

        var token = await context.FirstStepExternallyAsync();
        using var recovered = await context.SecondStepAsync(new { Token = token, RecoveryCode = last });

        using (var reused = await recovered.PostAsJsonAsync(SetupUrl, new { RecoveryCode = last }))
            Assert.Equal("Auth:TwoFactorCodeInvalid", await ErrorCodeAsync(reused));
        using (var bare = await recovered.PostAsJsonAsync(SetupUrl, new { }))
            Assert.Equal("Auth:TwoFactorAlreadyEnabled", await ErrorCodeAsync(bare));
        Assert.True((await context.ReadUserAsync(userId)).TwoFactorEnabled);
    }

    // 解绑最后一种登录方式注定被拒：先于确认身份拒绝，开久了的会话也直接得到"先设置密码"，不消耗凭证
    [Fact]
    public async Task Unlinking_the_last_sign_in_method_is_refused_before_any_confirmation()
    {
        using var context = Context.Create();
        context.Provider.User = External("gh-last-link");
        using var session = await context.SignInExternallyAsync();
        context.Clock.Advance(StaleSessionAge);
        using var links = JsonDocument.Parse(await session.GetStringAsync("/api/v1/external-auth/links"));
        var linkId = links.RootElement.GetProperty("providers").EnumerateArray()
            .Single(p => p.GetProperty("provider").GetString() == "github").GetProperty("link").GetProperty("id").GetGuid();

        using var rejected = await session.DeleteAsync($"/api/v1/external-auth/links/{linkId}");

        Assert.Equal("ExternalAuth:LastSignInMethod", await ErrorCodeAsync(rejected));
    }

    private const string SetupUrl = "/api/v1/auth/me/two-factor/setup";
    private const string DisableUrl = "/api/v1/auth/me/two-factor/disable";

    private static Task<HttpResponseMessage> SetPasswordAsync(HttpClient client, string password = NewPassword, string? twoFactorCode = null) =>
        client.PostAsJsonAsync("/api/v1/auth/me/password", new { NewPassword = password, ConfirmPassword = password, TwoFactorCode = twoFactorCode });

    private static ExternalUserInfo External(string id) => new()
    {
        ProviderId = id,
        ProviderAccountLabel = id,
        SuggestedUsername = id,
        Email = $"{id}@external.example.test"
    };

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>每个用例一个宿主：外部登录替身、手动推进的时钟、记下发出的安全提醒。</summary>
    private sealed class Context : IDisposable
    {
        private readonly ProjectWebApplicationFactory _factory;

        private Context(ProjectWebApplicationFactory factory, ExternalOAuthBackchannel provider)
        {
            _factory = factory;
            Provider = provider;
            Host = provider.CreateHost(factory, businessData: true).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                InterleavingGate.Wrap<ISecurityAlertPublisher>(services, inner => new RecordingAlerts(inner, Alerts));
            }));
        }

        public ExternalOAuthBackchannel Provider { get; }

        public WebApplicationFactory<Program> Host { get; }

        // 从此刻起步：缓存条目按真实时钟回收，假时钟落在过去会让刚写入的挑战被缓存当成已过期
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public AlertLog Alerts { get; } = new();

        public static Context Create() => new(new ProjectWebApplicationFactory(), new ExternalOAuthBackchannel());

        public void Step() => Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        public string Code(string secret) => Totp.ComputeCode(Totp.ParseSecret(secret)!, Totp.TimeStepAt(Clock.GetUtcNow()));

        /// <summary>走一遍外部登录，得到直接签发的会话（账号没开两步验证时）。</summary>
        public async Task<HttpClient> SignInExternallyAsync()
        {
            using var complete = await CompleteExternallyAsync();
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            using var body = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
            var client = ProjectWebApplicationFactory.CreateProjectClient(Host);
            client.DefaultRequestHeaders.Add("Cookie", CookieOf(complete));
            return client;
        }

        /// <summary>走一遍外部登录的第一步，返回第二步凭据（账号已开两步验证时）。</summary>
        public async Task<string> FirstStepExternallyAsync()
        {
            using var complete = await CompleteExternallyAsync();
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            using var body = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
            return body.RootElement.GetProperty("twoFactorToken").GetString()!;
        }

        public async Task<HttpClient> SecondStepAsync(object input)
        {
            var client = ProjectWebApplicationFactory.CreateProjectClient(Host);
            using var response = await client.PostAsJsonAsync("/api/v1/auth/two-factor", input);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Add("Cookie", CookieOf(response));
            return client;
        }

        /// <summary>在会话刚认证过时启用两步验证；返回密钥与恢复码。</summary>
        public async Task<(string Secret, List<string> RecoveryCodes)> EnableTwoFactorAsync(HttpClient session)
        {
            using var begin = await session.PostAsJsonAsync(SetupUrl, new { });
            Assert.True(begin.StatusCode == HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
            using var setup = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
            var secret = setup.RootElement.GetProperty("secret").GetString()!;
            using var enable = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/enable",
                new { SetupId = setup.RootElement.GetProperty("setupId").GetGuid(), Code = Code(secret) });
            Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
            using var codes = JsonDocument.Parse(await enable.Content.ReadAsStringAsync());
            return (secret, codes.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()!).ToList());
        }

        public async Task<JsonElement> MeAsync(HttpClient client)
        {
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            return me.RootElement.Clone();
        }

        public async Task<User> ReadUserAsync(Guid userId)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            return await db.Set<User>().IgnoreQueryFilters().AsNoTracking().SingleAsync(user => user.Id == userId);
        }

        /// <summary>只留下一个恢复码，模拟其余的都已用掉。</summary>
        public async Task KeepOnlyRecoveryCodeAsync(Guid userId, string recoveryCode)
        {
            using var scope = Host.Services.CreateScope();
            using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
            var user = await repository.GetByIdAsync(userId);
            user!.ReplaceRecoveryCodes([RecoveryCodes.Hash(recoveryCode)]);
            await repository.UpdateAsync(user);
            await unitOfWork.CompleteAsync();
        }

        public void Dispose()
        {
            Host.Dispose();
            _factory.Dispose();
        }

        private async Task<HttpResponseMessage> CompleteExternallyAsync()
        {
            using var client = ProjectWebApplicationFactory.CreateProjectClient(Host);
            var (_, cookie) = await ExternalOAuthBackchannel.StartAsync(client);
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            return await client.PostAsJsonAsync("/api/v1/external-auth/github/complete", new { });
        }

        private static string CookieOf(HttpResponseMessage response) =>
            string.Join("; ", response.Headers.GetValues("Set-Cookie")
                .Where(value => !value.StartsWith($"{StateCookieName}=", StringComparison.Ordinal))
                .Select(value => value.Split(';', 2)[0]));
    }

    /// <summary>按用户记下真正交给发布方的提醒种类。</summary>
    private sealed class AlertLog
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<SecurityAlertKind>> _alerts = new();

        public void Add(Guid userId, SecurityAlertKind kind) => _alerts.GetOrAdd(userId, _ => new()).Enqueue(kind);

        public SecurityAlertKind[] For(Guid userId) => _alerts.TryGetValue(userId, out var kinds) ? kinds.ToArray() : [];
    }

    private sealed class RecordingAlerts(ISecurityAlertPublisher inner, AlertLog log) : ISecurityAlertPublisher
    {
        public Task PublishAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken = default)
        {
            log.Add(userId, alert.Kind);
            return inner.PublishAsync(userId, alert, cancellationToken);
        }
    }
}
#endif
