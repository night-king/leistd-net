using Leistd.ServiceClient.Handlers;
using Leistd.ServiceClient.Options;
using Leistd.ServiceClient.Refit;
using Leistd.ServiceClient.Tests.TestDoubles;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Refit;
using Xunit;

namespace Leistd.ServiceClient.Tests.Refit;

/// <summary><c>AddRefitServiceClient</c> 的注册面：与手写客户端同一套按服务名登记的规则。</summary>
public sealed class RefitRegistrationTests
{
    private static ServiceCollection Services(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
        return services;
    }

    [Fact]
    public void Registration_adds_the_refit_client_and_one_standard_pipeline()
    {
        var services = Services(new() { ["Leistd:ServiceClients:Ping:BaseAddress"] = "http://ping" });

        services.AddRefitServiceClient<IPingApi, PingApiOptions>("Ping");

        services.AssertSingle<IPingApi>(ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IPingApi>());
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Ping"));
    }

    [Fact]
    public void Configuration_is_bound_first_and_the_delegate_overrides_it()
    {
        var services = Services(new() { ["Downstream:Ping:BaseAddress"] = "http://configured" });

        services.AddRefitServiceClient<IPingApi, PingApiOptions>(
            "Ping", o => o.BaseAddress = "http://code", configSectionPath: "Downstream:Ping");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("http://code", provider.GetRequiredService<IOptions<PingApiOptions>>().Value.BaseAddress);
    }

    [Fact]
    public void Repeating_the_same_registration_adds_no_service()
    {
        ServiceCollectionAssertions.AssertIdempotent(services =>
            services.AddRefitServiceClient<IPingApi, PingApiOptions>("Ping"));
    }

    // 处理器累加在工厂选项上，描述符层面看不出重复，要数构建出的链
    [Fact]
    public void Repeating_the_same_registration_keeps_one_pipeline_and_returns_the_same_client()
    {
        var services = Services(new() { ["Leistd:ServiceClients:Ping:BaseAddress"] = "http://ping" });
        services.AddRefitServiceClient<IPingApi, PingApiOptions>("Ping");

        var repeated = services.AddRefitServiceClient<IPingApi, PingApiOptions>("Ping");

        Assert.Equal("Ping", repeated.Name);
        services.AssertSingle<IPingApi>(ServiceLifetime.Transient);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Ping"));
    }

    // Refit 与手写客户端共用按服务名登记的规则：同名的另一种客户端在注册时就被拒
    [Fact]
    public void A_hand_written_client_under_the_same_name_is_rejected()
    {
        var services = Services();
        services.AddRefitServiceClient<IPingApi, PingApiOptions>("Ping");

        Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceClient<IHandWrittenPing, HandWrittenPing, OtherOptions>("Ping"));
    }

    // 只换 Refit 接口、选项与配置节不变：同名下的第二个接口不会被登记，必须在注册时拒绝而不是解析时才失败
    [Fact]
    public void Another_refit_interface_under_the_same_name_is_rejected()
    {
        var services = Services();
        services.AddRefitServiceClient<IPingApi, PingApiOptions>("Ping");

        Assert.Throws<InvalidOperationException>(() =>
            services.AddRefitServiceClient<IPongApi, PingApiOptions>("Ping"));
    }

    [Fact]
    public void A_second_service_name_with_its_own_options_coexists()
    {
        var services = Services(new()
        {
            ["Leistd:ServiceClients:Ping:BaseAddress"] = "http://ping",
            ["Leistd:ServiceClients:Other:BaseAddress"] = "http://other",
        });

        services.AddRefitServiceClient<IPingApi, PingApiOptions>("Ping");
        services.AddServiceClient<IHandWrittenPing, HandWrittenPing, OtherOptions>("Other");

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Ping"));
        Assert.Equal(1, HandlerChain.Count<TransportFailureHandler>(provider, "Other"));
    }

    public sealed class PingApiOptions : ServiceClientOptions;

    public sealed class OtherOptions : ServiceClientOptions;

    public interface IPingApi
    {
        [Get("/ping")]
        Task<string> PingAsync(CancellationToken cancellationToken = default);
    }

    public interface IPongApi
    {
        [Get("/pong")]
        Task<string> PongAsync(CancellationToken cancellationToken = default);
    }

    public interface IHandWrittenPing;

    private sealed class HandWrittenPing(HttpClient http) : IHandWrittenPing
    {
        public HttpClient Http { get; } = http;
    }
}
