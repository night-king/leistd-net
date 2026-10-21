using Leistd.AmbientContext;
using Leistd.TestBase.Assertions;
using Leistd.Tracing.Abstractions;
using Leistd.Tracing.AspNetCore;
using Leistd.Tracing.Options;
using Leistd.Tracing.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Tracing.Tests.Core;

/// <summary><c>AddCorrelationIdCore</c> / <c>AddCorrelationId</c> 的注册面。</summary>
/// <remarks>
/// 两个入口常被不同组件各调一次（服务客户端、Hub 基座、宿主自身）：重复登记会让环境上下文贡献者出现两次、
/// 校验失败报两遍；宿主先换掉的提供器也不能被组件换回来。
/// </remarks>
public sealed class CorrelationIdRegistrationTests
{
    private static IServiceCollection Base() =>
        new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

    [Fact]
    public void Registration_adds_the_provider_and_the_ambient_contributor()
    {
        var services = Base();

        services.AddCorrelationIdCore();

        services.AssertSingle<ICorrelationIdProvider>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<ICorrelationIdProvider, CorrelationIdProvider>();
        services.AssertSingle<IAmbientContextContributor>(ServiceLifetime.Transient);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddCorrelationIdCore());

    // 验证器不在 AssertIdempotent 的计数范围内：登记两份时同一条失败会报两遍
    [Fact]
    public void Repeated_registration_keeps_one_options_validator()
    {
        var services = Base();

        services.AddCorrelationIdCore().AddCorrelationIdCore();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<CorrelationIdOptions>));
    }

    // Web 入口只是转调核心入口：两者以任意顺序同时出现时仍只有一份注册
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_web_entry_and_the_core_entry_coexist_without_duplicates(bool webFirst)
    {
        var services = Base();

        if (webFirst)
        {
            services.AddCorrelationId().AddCorrelationIdCore();
        }
        else
        {
            services.AddCorrelationIdCore().AddCorrelationId();
        }

        services.AssertSingle<ICorrelationIdProvider>(ServiceLifetime.Singleton);
        services.AssertSingle<IAmbientContextContributor>(ServiceLifetime.Transient);
    }

    [Fact]
    public void A_host_registered_provider_is_kept()
    {
        var services = Base();
        services.AddSingleton<ICorrelationIdProvider, HostProvider>();

        services.AddCorrelationIdCore();

        services.AssertResolvesTo<ICorrelationIdProvider, HostProvider>();
    }

    private sealed class HostProvider : ICorrelationIdProvider
    {
        public string? Get() => "host";

        public IDisposable Change(string correlationId) => throw new NotSupportedException();
    }
}
