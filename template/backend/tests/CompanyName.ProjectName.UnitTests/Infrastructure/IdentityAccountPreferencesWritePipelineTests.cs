#if (RemoteTokenAuth)
using System.Net;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Infrastructure;
using CompanyName.ProjectName.Infrastructure.AccountPreferences;
using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

/// <summary>代表本人写偏好的客户端：有时限、不重放。Identity 挂起时按依赖超时失败，而不是占住请求等到 HttpClient 的 100 秒兜底。</summary>
public sealed class IdentityAccountPreferencesWritePipelineTests
{
    private const string ClientName = "IdentityAccountPreferencesUser";

    [Fact]
    public async Task A_hanging_Identity_fails_the_write_as_a_dependency_timeout()
    {
        var calls = 0;
        await using var provider = Build(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var error = await Assert.ThrowsAsync<ServiceClientException>(() => Write(provider));

        Assert.Equal(ServiceClientFailureKind.Timeout, error.FailureKind);
        Assert.Equal(1, calls);
    }

    // 卡在 Token Exchange 取令牌（签发方挂起）也在时限之内：管道包住交换，而不是只包最终的 PATCH
    [Fact]
    public async Task A_write_stuck_in_token_exchange_fails_as_a_dependency_timeout()
    {
        await using var provider = Build(
            (_, _) => throw new InvalidOperationException("The PATCH must not be sent before a token is exchanged."),
            withTokenExchange: true);

        var error = await Assert.ThrowsAsync<ServiceClientException>(() => Write(provider));

        Assert.Equal(ServiceClientFailureKind.Timeout, error.FailureKind);
    }

    // 写入不是幂等的：暂时性失败也只发一次，是否已保存由下次读取对齐
    [Fact]
    public async Task A_transient_failure_is_not_replayed()
    {
        var calls = 0;
        await using var provider = Build((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        using var response = await Write(provider);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, calls);
    }

    private static Task<HttpResponseMessage> Write(IServiceProvider provider)
        => provider.GetRequiredService<IIdentityAccountPreferencesWriteApi>()
            .PatchAsync(new JsonObject { ["theme"] = "dark" }, acceptLanguage: null);

    private static ServiceProvider Build(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        bool withTokenExchange = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Leistd:ServiceClients:Identity:BaseAddress"] = "http://identity.test/",
        };
        if (withTokenExchange)
        {
            settings["Leistd:ServiceAuth:Authority"] = "http://identity.test/";
            settings["Leistd:ServiceAuth:ClientId"] = "resource";
            settings["Leistd:ServiceAuth:ClientSecret"] = "test-secret";
            settings["Leistd:ServiceClients:Identity:TokenExchange:Audience"] = "identity-api";
            settings["Leistd:ServiceClients:Identity:TokenExchange:Scope"] = "identity-api";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        // 宿主自带的配置：服务客户端在创建 HttpClient 时按它绑定地址
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructureServices(configuration);
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond))
            // 外层兜底调短：没有弹性管道时测试很快以兜底的取消失败，而不是等 100 秒
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(5));
        // 交换处理器先取当前用户的令牌：让它挂起，等同于签发阶段卡住
        services.AddSingleton<IUserAccessTokenAccessor>(new HangingUserTokenAccessor());
        services.Configure<HttpStandardResilienceOptions>($"{ClientName}-standard", options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(200);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(1);
        });
        return services.BuildServiceProvider();
    }

    private sealed class HangingUserTokenAccessor : IUserAccessTokenAccessor
    {
        public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
#endif
