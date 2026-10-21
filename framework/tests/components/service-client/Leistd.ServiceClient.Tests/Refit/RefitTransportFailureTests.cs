using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Options;
using Leistd.ServiceClient.Refit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Refit;
using Xunit;

namespace Leistd.ServiceClient.Tests.Refit;

public sealed class RefitTransportFailureTests
{
    [Theory]
    [InlineData(ServiceClientFailureKind.Configuration, 500)]
    [InlineData(ServiceClientFailureKind.Unknown, 500)]
    [InlineData(ServiceClientFailureKind.InvalidResponse, 502)]
    [InlineData(ServiceClientFailureKind.RemoteFailure, 502)]
    [InlineData(ServiceClientFailureKind.Unavailable, 503)]
    [InlineData(ServiceClientFailureKind.Timeout, 504)]
    public async Task Classified_pipeline_failures_reach_the_http_boundary(
        ServiceClientFailureKind kind, int expectedStatus)
    {
        var failure = new ServiceClientException("private upstream details", failureKind: kind);
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                Register(services, new FailureHandler(failure));
                // 客户端登记自己的映射，宿主不补写组件映射。
                services.AddGlobalExceptionHandler(_ => { });
            })
            .Configure(app =>
            {
                app.UseGlobalExceptionHandler();
                app.Run(async context =>
                {
                    await context.RequestServices.GetRequiredService<IFailureApi>().GetAsync();
                });
            })).StartAsync();

        using var response = await host.GetTestClient().GetAsync("/");
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.DoesNotContain("private upstream", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_failures_keep_the_component_classification(bool wrappedResponse)
    {
        var cause = new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
        using var provider = Build(new FailureHandler(cause));

        var failure = await Assert.ThrowsAsync<ServiceClientException>(
            () => InvokeAsync(provider.GetRequiredService<IFailureApi>(), wrappedResponse));

        Assert.Equal(ServiceClientFailureKind.Unavailable, failure.FailureKind);
        Assert.Same(cause, failure.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Classified_authentication_failures_are_not_wrapped_again(bool wrappedResponse)
    {
        var cause = new ServiceClientException("token exchange rejected",
            failureKind: ServiceClientFailureKind.RemoteFailure);
        using var provider = Build(new FailureHandler(cause));

        var failure = await Assert.ThrowsAsync<ServiceClientException>(
            () => InvokeAsync(provider.GetRequiredService<IFailureApi>(), wrappedResponse));

        Assert.Same(cause, failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_keeps_the_native_contract(bool wrappedResponse)
    {
        var handler = new WaitingHandler();
        using var provider = Build(handler);
        using var cancellation = new CancellationTokenSource();
        var call = InvokeAsync(provider.GetRequiredService<IFailureApi>(), wrappedResponse, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Client_timeout_keeps_the_native_contract()
    {
        using var provider = Build(new WaitingHandler(), builder => builder.ConfigureHttpClient(
            client => client.Timeout = TimeSpan.FromMilliseconds(200)));

        var failure = await Assert.ThrowsAsync<TaskCanceledException>(
            () => provider.GetRequiredService<IFailureApi>().GetAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsType<TimeoutException>(failure.InnerException);
    }

    [Fact]
    public async Task Resilience_timeout_keeps_the_component_classification()
    {
        using var provider = Build(new WaitingHandler(), builder => builder.AddResilienceHandler(
            "timeout", pipeline => pipeline.AddTimeout(TimeSpan.FromMilliseconds(200))));

        var failure = await Assert.ThrowsAsync<ServiceClientException>(
            () => provider.GetRequiredService<IFailureApi>().GetAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(ServiceClientFailureKind.Timeout, failure.FailureKind);
    }

    private static ServiceProvider Build(HttpMessageHandler handler, Action<IHttpClientBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var builder = Register(services, handler);
        configure?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    private static IHttpClientBuilder Register(IServiceCollection services, HttpMessageHandler handler) =>
        services.AddRefitServiceClient<IFailureApi, FailureOptions>(
                "Failure", options => options.BaseAddress = "http://failure-service")
            .ConfigurePrimaryHttpMessageHandler(() => handler);

    private static async Task InvokeAsync(IFailureApi api, bool wrappedResponse,
        CancellationToken cancellationToken = default)
    {
        if (wrappedResponse)
        {
            using var response = await api.GetResponseAsync(cancellationToken);
        }
        else
        {
            await api.GetAsync(cancellationToken);
        }
    }

    public sealed class FailureOptions : ServiceClientOptions;

    public interface IFailureApi
    {
        [Get("/probe")]
        Task<string> GetAsync(CancellationToken cancellationToken = default);

        [Get("/probe")]
        Task<ApiResponse<string>> GetResponseAsync(CancellationToken cancellationToken = default);
    }

    private sealed class FailureHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
