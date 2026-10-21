using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.ServiceClient;
using Leistd.MultiTenancy.ServiceClient.Options;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.MultiTenancy.Tests.ServiceClient;

/// <summary>远端连接存储的注册契约：它是连接配置的唯一权威来源，相同参数幂等，换服务名或配置节即拒绝。</summary>
/// <remarks>
/// 两套参数同时生效时，路由取哪个控制面由注册顺序决定——那决定的是租户数据落在哪个库，必须在注册时就报错。
/// </remarks>
public sealed class RemoteTenantConnectionStoreRegistrationTests
{
    private static IServiceCollection Base(IDictionary<string, string?>? settings = null) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
                .Build());

    [Fact]
    public void Registration_exposes_the_store_and_the_database_directory()
    {
        var services = Base();

        var builder = services.AddRemoteTenantConnectionStore("Identity");

        Assert.Equal("Identity", builder.Name);
        services.AssertSingle<ITenantConnectionConfigurationStore>(ServiceLifetime.Transient);
        services.AssertSingle<ITenantDatabaseDirectory>(ServiceLifetime.Transient);
    }

    [Fact]
    public void Registering_it_twice_with_the_same_arguments_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddRemoteTenantConnectionStore("Identity"));

    // AssertIdempotent 不看验证器与客户端管道：验证器叠加会让同一条失败报两遍，处理器叠加会让每次回源多走一遍管道
    [Fact]
    public void Repeated_registration_keeps_one_validator_and_one_handler_pipeline()
    {
        var settings = new Dictionary<string, string?> { ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.test" };
        var once = Base(settings);
        once.AddRemoteTenantConnectionStore("Identity");
        var twice = Base(settings);
        var first = twice.AddRemoteTenantConnectionStore("Identity");
        var second = twice.AddRemoteTenantConnectionStore("Identity");

        Assert.Equal(first.Name, second.Name);
        Assert.Single(twice, d => d.ServiceType == typeof(IValidateOptions<RemoteTenantConnectionClientOptions>));
        Assert.Equal(HandlerCount(once), HandlerCount(twice));
    }

    [Theory]
    [InlineData("Control", null)]
    [InlineData("Identity", "Tenancy:ControlPlane")]
    public void A_second_registration_with_other_arguments_is_rejected(string serviceName, string? configSectionPath)
    {
        var services = Base();
        services.AddRemoteTenantConnectionStore("Identity");

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddRemoteTenantConnectionStore(serviceName, configSectionPath: configSectionPath));

        Assert.Contains("exactly one authoritative source", error.Message, StringComparison.Ordinal);
    }

    // 消息里的键名要与实际绑定的配置节一致，照着它补配置才能起来
    [Theory]
    [InlineData("Identity", null, "Leistd:ServiceClients:Identity:BaseAddress")]
    [InlineData("Control", null, "Leistd:ServiceClients:Control:BaseAddress")]
    [InlineData("Identity", "Tenancy:ControlPlane", "Tenancy:ControlPlane:BaseAddress")]
    public void Missing_base_address_names_the_actual_configuration_key(string serviceName, string? configSectionPath, string expectedKey)
    {
        using var provider = Base()
            .AddRemoteTenantConnectionStore(serviceName, configSectionPath: configSectionPath).Services
            .BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RemoteTenantConnectionClientOptions>>().Value);

        Assert.StartsWith(expectedKey, Assert.Single(failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_section_is_bound_and_the_delegate_applies_after_it()
    {
        using var provider = Base(new Dictionary<string, string?>
            {
                ["Tenancy:ControlPlane:BaseAddress"] = "https://from-config.test",
                ["Tenancy:ControlPlane:RoutePrefix"] = "/config-prefix",
            })
            .AddRemoteTenantConnectionStore("Identity", o => o.BaseAddress = "https://from-code.test", "Tenancy:ControlPlane").Services
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RemoteTenantConnectionClientOptions>>().Value;

        Assert.Equal("https://from-code.test", options.BaseAddress);
        Assert.Equal("/config-prefix", options.RoutePrefix);
    }

    // 返回的构建器就是存储所用的命名客户端：宿主在上面追加的配置要落到回源请求上
    [Fact]
    public void The_returned_builder_configures_the_store_client()
    {
        var services = Base(new Dictionary<string, string?> { ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.test" });

        services.AddRemoteTenantConnectionStore("Identity")
            .ConfigureHttpClient(client => client.DefaultRequestHeaders.Add("X-Probe", "1"));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Identity");
        Assert.True(client.DefaultRequestHeaders.Contains("X-Probe"));
    }

    private static int HandlerCount(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("Identity");
        var count = 0;
        while (handler is DelegatingHandler delegating)
        {
            count++;
            handler = delegating.InnerHandler!;
        }

        return count;
    }
}
