#if (RemoteTokenAuth)
using System.Net;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Domain.Users.Abstractions;
using CompanyName.ProjectName.Infrastructure.AccountPreferences;
using Leistd.ServiceClient.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

/// <summary>账户偏好端口的写入：以本人委托转交 Identity，业务拒绝同码同状态交还，其余失败按依赖故障处理。</summary>
public sealed class IdentityAccountPreferencesTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    [Fact]
    public async Task A_successful_write_returns_and_caches_Identitys_representation()
    {
        var (port, cache) = Create(_ => Response(HttpStatusCode.OK, """{"preferences":{"theme":"dark"},"preferenceOverrides":{"theme":"dark"}}""", "\"v2\""));

        var result = await port.WriteForCurrentUserAsync(null, UserId, "theme", "dark");

        Assert.Equal("dark", result.Preferences.Theme);
        // 写入后本实例直接用新表示，不再回读（读取接口一调就抛）
        Assert.Equal("dark", (await cache.ReadServerAsync(new AccountPreferenceCacheKey(null, UserId), default))?.Preferences.Theme);
    }

    [Fact]
    public async Task A_business_rejection_from_Identity_keeps_its_code()
    {
        var (port, _) = Create(_ => Response(HttpStatusCode.BadRequest, """{"status":400,"code":"AppSetting:TimeZoneInvalid","detail":"bad"}"""));

        var error = await Assert.ThrowsAsync<RemoteBusinessException>(() => port.WriteForCurrentUserAsync(null, UserId, "timeZone", "Asia/Tokyo"));

        Assert.Equal("AppSetting:TimeZoneInvalid", error.Code);
        // 说明只取 Identity 的公开 detail，不是带地址与 traceId 的诊断消息
        Assert.Equal("bad", error.Message);
        Assert.Equal(400, error.HttpStatusCode);
    }

    // 带业务码的 403 是 Identity 明确的业务禁止：同码同状态交还，不当作依赖故障
    [Fact]
    public async Task A_business_forbidden_from_Identity_keeps_its_status()
    {
        var (port, _) = Create(_ => Response(HttpStatusCode.Forbidden, """{"status":403,"code":"Setting:IdentityCannotOperate","detail":"no"}"""));

        var error = await Assert.ThrowsAsync<RemoteBusinessException>(() => port.WriteForCurrentUserAsync(null, UserId, "theme", "dark"));

        Assert.Equal(403, error.HttpStatusCode);
    }

    // 401 即使带业务码也不交还：转成本地 401 会让会话仍有效的用户被前端登出
    [Fact]
    public async Task An_unauthorized_answer_stays_a_dependency_failure_even_with_a_code()
    {
        var (port, _) = Create(_ => Response(HttpStatusCode.Unauthorized, """{"status":401,"code":"Auth:SessionExpired","detail":"expired"}"""));

        await Assert.ThrowsAsync<RemoteServiceException>(() => port.WriteForCurrentUserAsync(null, UserId, "theme", "dark"));
    }

    // 分不清用户凭证失效与交换配置错误：Identity 的 401/403 按依赖故障处理，不转成本地 401 让前端登出
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Other_failures_stay_dependency_failures(HttpStatusCode status)
    {
        var (port, _) = Create(_ => Response(status, """{"status":0}"""));

        var error = await Assert.ThrowsAsync<RemoteServiceException>(() => port.WriteForCurrentUserAsync(null, UserId, "theme", "dark"));

        Assert.Equal((int)status, error.RemoteStatusCode);
    }

    // 机器身份被拒（漏授 scope、密钥轮换）：/me 照常返回、偏好按暂时读不到；给收件人产出文本的服务端读取仍然失败
    [Fact]
    public async Task A_machine_rejection_leaves_only_the_current_users_preferences_unknown()
    {
        var (port, _) = Create(_ => throw new InvalidOperationException("No write expected."),
            new FixedReadApi(() => Response(HttpStatusCode.Forbidden, """{"status":403}""")));

        Assert.Null(await port.ReadAsync(null, UserId, AccountPreferenceReadMode.CurrentUser));
        await Assert.ThrowsAsync<RemoteServiceException>(() => port.ReadAsync(null, UserId, AccountPreferenceReadMode.Server));
    }

    private static (IdentityAccountPreferences Port, IdentityAccountPreferenceCache Cache) Create(
        Func<JsonObject, Task<HttpResponseMessage>> respond,
        IIdentityAccountPreferencesReadApi? readApi = null)
    {
        var services = new ServiceCollection();
        services.AddOptions<AccountPreferencesClientOptions>();
        services.AddSingleton(readApi ?? new UnusedReadApi());
        var provider = services.BuildServiceProvider();
        var cache = new IdentityAccountPreferenceCache(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<AccountPreferencesClientOptions>>(),
            TimeProvider.System,
            NullLogger<IdentityAccountPreferenceCache>.Instance);
        return (new IdentityAccountPreferences(new RecordingWriteApi(respond), cache, NullLogger<IdentityAccountPreferences>.Instance), cache);
    }

    private static Task<HttpResponseMessage> Response(HttpStatusCode status, string json, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        }

        return Task.FromResult(response);
    }

    private sealed class RecordingWriteApi(Func<JsonObject, Task<HttpResponseMessage>> respond) : IIdentityAccountPreferencesWriteApi
    {
        public Task<HttpResponseMessage> PatchAsync(JsonObject body, string? acceptLanguage, CancellationToken cancellationToken = default)
            => respond(body);
    }

    private sealed class FixedReadApi(Func<Task<HttpResponseMessage>> respond) : IIdentityAccountPreferencesReadApi
    {
        public Task<HttpResponseMessage> GetAsync(string tenant, Guid userId, string? ifNoneMatch, CancellationToken cancellationToken = default)
            => respond();
    }

    private sealed class UnusedReadApi : IIdentityAccountPreferencesReadApi
    {
        public Task<HttpResponseMessage> GetAsync(string tenant, Guid userId, string? ifNoneMatch, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A write must not read back from Identity.");
    }
}
#endif
