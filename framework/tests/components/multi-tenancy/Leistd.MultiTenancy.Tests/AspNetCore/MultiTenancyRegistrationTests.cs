using Leistd.AmbientContext;
using Leistd.MultiTenancy.AspNetCore;
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Resolution;
using Leistd.MultiTenancy.Stores;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary><c>AddMultiTenancy()</c> 的注册面：补齐 Web 宿主所需服务，重复调用不叠加验证器、贡献者与解析链。</summary>
public sealed class MultiTenancyRegistrationTests
{
    private static IServiceCollection Base() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

    [Fact]
    public void Registration_brings_in_the_core_services_and_the_http_context_accessor()
    {
        var services = Base().AddMultiTenancy();

        services.AssertSingle<ICurrentTenant>(ServiceLifetime.Transient);
        services.AssertSingle<ITenantResolver>(ServiceLifetime.Scoped);
        services.AssertSingle<IHttpContextAccessor>(ServiceLifetime.Singleton);
        Assert.Single(services, d => d.ServiceType == typeof(IAmbientContextContributor));
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddMultiTenancy());

    // AssertIdempotent 不看验证器与选项配置：验证器叠加时每条失败报两遍，解析链叠加时每个贡献者跑两遍
    [Fact]
    public void Repeated_registration_keeps_one_validator_set_and_one_resolve_chain()
    {
        var services = Base().AddMultiTenancy();
        var validators = services.Count(d => d.ServiceType == typeof(IValidateOptions<MultiTenancyOptions>));

        services.AddMultiTenancy();
        // 注册表校验默认开启，构建选项时要求有存储；这里只看注册面
        services.AddSingleton<ITenantStore>(_ => throw new NotSupportedException());

        Assert.Equal(validators, services.Count(d => d.ServiceType == typeof(IValidateOptions<MultiTenancyOptions>)));
        Assert.Single(services, d => d.ServiceType == typeof(IPostConfigureOptions<TenantResolveOptions>));
        using var provider = services.BuildServiceProvider();
        var contributors = provider.GetRequiredService<IOptions<TenantResolveOptions>>().Value.Contributors;
        Assert.Equal(4, contributors.Count);
        Assert.Equal(contributors.Count, contributors.Select(c => c.GetType()).Distinct().Count());
    }

    // 宿主替换上下文访问器时 Web 集成同样保留它
    [Fact]
    public void A_host_tenant_accessor_is_kept()
    {
        var services = Base();
        services.AddSingleton<ICurrentTenantAccessor>(_ => throw new NotSupportedException());

        services.AddMultiTenancy();

        Assert.NotNull(services.AssertSingle<ICurrentTenantAccessor>(ServiceLifetime.Singleton).ImplementationFactory);
    }
}
