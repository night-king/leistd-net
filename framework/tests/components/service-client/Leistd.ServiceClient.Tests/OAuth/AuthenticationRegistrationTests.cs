using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.OAuth;
using Leistd.ServiceClient.OAuth.Handlers;
using Leistd.ServiceClient.OAuth.Options;
using Leistd.ServiceClient.Tests.TestDoubles;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.ServiceClient.Tests.OAuth;

/// <summary>服务认证入口的注册面：工作负载身份只有一个，每个命名客户端只有一种认证方式，相同登记重复调用不叠加。</summary>
/// <remarks>
/// 验证器与处理器都不在 <c>AssertIdempotent</c> 的计数范围内：验证器登记两份时每条失败报两遍，
/// 处理器挂两层时每个请求取两次令牌。这里直接数验证器、失败条数和构建出的处理器链。
/// </remarks>
public sealed class AuthenticationRegistrationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        return services;
    }

    private static void ValidIdentity(ServiceAuthenticationOptions options)
    {
        options.Authority = "http://identity.test/";
        options.ClientId = "orders-api";
        options.ClientSecret = "secret";
    }

    [Fact]
    public void Service_authentication_registers_the_token_cache_as_a_singleton()
    {
        var services = Services();

        services.AddServiceAuthentication(ValidIdentity);

        services.AssertSingle<TokenCache>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Repeated_service_authentication_adds_no_service()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddServiceAuthentication(ValidIdentity));
    }

    [Fact]
    public void Repeated_service_authentication_keeps_one_validator_and_reports_each_failure_once()
    {
        var services = Services();

        services.AddServiceAuthentication().AddServiceAuthentication();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<ServiceAuthenticationOptions>));
        using var provider = services.BuildServiceProvider();
        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ServiceAuthenticationOptions>>().Value);
        Assert.Equal(3, failure.Failures.Count());
    }

    // 组合根拆分时后一处的委托照样生效，而不是被"已经注册过"吞掉
    [Fact]
    public void Repeated_service_authentication_applies_the_later_delegate()
    {
        var services = Services();

        services.AddServiceAuthentication(ValidIdentity).AddServiceAuthentication(o => o.ClientId = "billing-api");

        using var provider = services.BuildServiceProvider();
        Assert.Equal("billing-api", provider.GetRequiredService<IOptions<ServiceAuthenticationOptions>>().Value.ClientId);
    }

    // 本服务只有一个工作负载身份：第二个配置节要么被静默忽略、要么两节互相覆盖，都不是宿主想要的
    [Fact]
    public void Service_authentication_with_another_section_is_rejected()
    {
        var services = Services().AddServiceAuthentication();

        Assert.Throws<InvalidOperationException>(() => services.AddServiceAuthentication(configSectionPath: "Other:Auth"));
    }

    [Fact]
    public void Repeated_client_credentials_install_one_handler()
    {
        var services = Services().AddServiceAuthentication(ValidIdentity);

        services.AddHttpClient("Identity").AddClientCredentials(o => o.Scope = "a");
        services.AddHttpClient("Identity").AddClientCredentials(o => o.Scope = "b");

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<ClientCredentialsDelegatingHandler>(provider, "Identity"));
        Assert.Equal("b", provider.GetRequiredService<IOptionsMonitor<ClientCredentialsOptions>>().Get("Identity").Scope);
    }

    [Fact]
    public void Repeated_token_exchange_installs_one_handler_and_reports_each_failure_once()
    {
        var services = Services().AddServiceAuthentication(ValidIdentity);
        services.AddSingleton<IUserAccessTokenAccessor, NoUserToken>();

        services.AddHttpClient("Billing").AddTokenExchange();
        services.AddHttpClient("Billing").AddTokenExchange();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<TokenExchangeOptions>));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<TokenExchangeDelegatingHandler>(provider, "Billing"));
        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<TokenExchangeOptions>>().Get("Billing"));
        Assert.Equal(2, failure.Failures.Count());
    }

    // 一个客户端只能有一种身份：换方式或换配置节都意味着宿主对这个客户端有两套说法
    [Theory]
    [InlineData("credentials then exchange")]
    [InlineData("exchange then credentials")]
    [InlineData("credentials with another section")]
    [InlineData("exchange with another section")]
    public void A_second_authentication_for_the_same_client_is_rejected(string conflict)
    {
        var services = Services();
        var first = conflict.StartsWith("credentials", StringComparison.Ordinal);
        if (first)
        {
            services.AddHttpClient("Downstream").AddClientCredentials();
        }
        else
        {
            services.AddHttpClient("Downstream").AddTokenExchange();
        }

        Action second = conflict switch
        {
            "credentials then exchange" => () => services.AddHttpClient("Downstream").AddTokenExchange(),
            "exchange then credentials" => () => services.AddHttpClient("Downstream").AddClientCredentials(),
            "credentials with another section" => () => services.AddHttpClient("Downstream").AddClientCredentials(configSectionPath: "Other"),
            _ => () => services.AddHttpClient("Downstream").AddTokenExchange(configSectionPath: "Other"),
        };

        Assert.Throws<InvalidOperationException>(second);
    }

    // 认证按客户端名登记：另一个客户端选另一种方式互不影响
    [Fact]
    public void Different_clients_can_use_different_authentication()
    {
        var services = Services().AddServiceAuthentication(ValidIdentity);
        services.AddSingleton<IUserAccessTokenAccessor, NoUserToken>();

        services.AddHttpClient("Identity").AddClientCredentials();
        services.AddHttpClient("Billing").AddTokenExchange();

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, HandlerChain.Count<ClientCredentialsDelegatingHandler>(provider, "Identity"));
        Assert.Equal(0, HandlerChain.Count<TokenExchangeDelegatingHandler>(provider, "Identity"));
        Assert.Equal(1, HandlerChain.Count<TokenExchangeDelegatingHandler>(provider, "Billing"));
    }

    private sealed class NoUserToken : IUserAccessTokenAccessor
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }
}
