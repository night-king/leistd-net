using Leistd.MultiTenancy.ConnectionStrings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary>远端解析的路由缓存配置节：默认 <c>Leistd:MultiTenancy:Routing</c>，可另指配置节，校验消息按实际路径报键。</summary>
public sealed class RoutingSectionTests
{
    private static ServiceProvider Build(IDictionary<string, string?> settings, string? configSectionPath = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        if (configSectionPath is null)
        {
            services.AddRemoteTenantConnectionResolution();
        }
        else
        {
            services.AddRemoteTenantConnectionResolution(configSectionPath);
        }

        return services.BuildServiceProvider();
    }

    private static TimeSpan CacheLifetime(ServiceProvider provider)
        => provider.GetRequiredService<IOptions<TenantRouteCacheOptions>>().Value.CacheLifetime;

    // 旧的顶层 TenantRouting 节不再读取：留着它不报错，TTL 回到默认值
    [Fact]
    public void The_default_section_is_under_leistd_and_the_old_section_is_ignored()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Leistd:MultiTenancy:Routing:CacheLifetime"] = "00:03:00",
            ["TenantRouting:CacheLifetime"] = "00:07:00",
        });

        Assert.Equal(TimeSpan.FromMinutes(3), CacheLifetime(provider));
    }

    [Fact]
    public void A_custom_section_is_bound()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Routing:CacheLifetime"] = "00:04:00" }, "Routing");

        Assert.Equal(TimeSpan.FromMinutes(4), CacheLifetime(provider));
    }

    [Theory]
    [InlineData(null, TenantRouteCacheOptions.SectionName)]
    [InlineData("Routing", "Routing")]
    public void Validation_failures_name_the_actual_section(string? configSectionPath, string expectedSection)
    {
        using var provider = Build(
            new Dictionary<string, string?> { [$"{expectedSection}:CacheLifetime"] = "02:00:00" },
            configSectionPath);

        var failure = Assert.Throws<OptionsValidationException>(() => CacheLifetime(provider));

        Assert.StartsWith($"{expectedSection}:CacheLifetime", Assert.Single(failure.Failures), StringComparison.Ordinal);
    }

    // 路由缓存只有一份：第二次换路径若被接受，校验消息会报一个没人配置的键
    [Fact]
    public void Repeated_registration_with_another_section_is_rejected()
    {
        var services = new ServiceCollection().AddRemoteTenantConnectionResolution();

        Assert.Throws<InvalidOperationException>(() => services.AddRemoteTenantConnectionResolution("Routing"));
    }

    // AssertIdempotent 不看验证器：同一路径重复调用只留一个
    [Fact]
    public void Repeated_registration_with_the_same_section_keeps_one_validator()
    {
        var services = new ServiceCollection()
            .AddRemoteTenantConnectionResolution()
            .AddRemoteTenantConnectionResolution();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<TenantRouteCacheOptions>));
    }
}
