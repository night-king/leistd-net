using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

/// <summary>超时的归属：可归类的超时来自宿主叠加的弹性管道，<c>HttpClient.Timeout</c> 只是按 .NET 原生契约抛出的外层兜底。</summary>
/// <remarks>
/// 弹性管道的超时在消息处理器之内生效，最外层的日志处理器能把它包装为 <see cref="ServiceClientFailureKind.Timeout"/>。
/// <c>HttpClient.Timeout</c> 在所有处理器之外生效：处理器看到的是已被取消的令牌，无法与调用方主动取消区分，
/// 因此原样抛出 .NET 的取消异常（内层为 <see cref="TimeoutException"/>），组件不包装、也不在 HTTP 边界认领它。
/// </remarks>
public sealed class ServiceClientTimeoutTests
{
    private static ServiceProvider Build(Action<IHttpClientBuilder> configure)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Leistd:ServiceClients:Slow:BaseAddress"] = "http://slow-service",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = services.AddServiceClient<ISlowClient, SlowClient, SlowClientOptions>("Slow")
            .ConfigurePrimaryHttpMessageHandler(() => new SlowHandler());
        configure(builder);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Resilience_timeout_is_classified_as_a_timeout_failure()
    {
        using var provider = Build(builder => builder.AddResilienceHandler(
            "timeout", pipeline => pipeline.AddTimeout(TimeSpan.FromMilliseconds(200))));

        var exception = await Assert.ThrowsAsync<ServiceClientException>(
            () => provider.GetRequiredService<ISlowClient>().GetAsync());

        Assert.Equal(ServiceClientFailureKind.Timeout, exception.FailureKind);
    }

    [Fact]
    public async Task Client_timeout_keeps_the_dotnet_contract()
    {
        using var provider = Build(builder => builder.ConfigureHttpClient(
            client => client.Timeout = TimeSpan.FromMilliseconds(200)));

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(
            () => provider.GetRequiredService<ISlowClient>().GetAsync());

        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    // 组件不设 HttpClient.Timeout：外层兜底保持 .NET 默认值，不与宿主弹性管道的总超时竞争
    [Fact]
    public void The_component_leaves_the_client_timeout_at_the_dotnet_default()
    {
        using var provider = Build(_ => { });

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Slow");
        using var reference = new HttpClient();

        Assert.Equal(reference.Timeout, client.Timeout);
    }

    // 调用方自己取消时保持原样：那是调用方的决定，不是服务故障
    [Fact]
    public async Task Caller_cancellation_is_not_turned_into_a_failure()
    {
        using var provider = Build(_ => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetRequiredService<ISlowClient>().GetAsync(cancellation.Token));
    }

    public sealed class SlowClientOptions : ServiceClientOptions;

    public interface ISlowClient
    {
        Task GetAsync(CancellationToken cancellationToken = default);
    }

    public sealed class SlowClient(HttpClient httpClient) : ISlowClient
    {
        public async Task GetAsync(CancellationToken cancellationToken = default)
        {
            using var response = await httpClient.GetAsync("slow", cancellationToken);
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
