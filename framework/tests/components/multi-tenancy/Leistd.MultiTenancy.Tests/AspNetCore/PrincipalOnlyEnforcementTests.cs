using Leistd.MultiTenancy.AspNetCore.Resolution;
using Leistd.MultiTenancy.AspNetCore;
using Leistd.MultiTenancy.AspNetCore.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Leistd.MultiTenancy.Resolution;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary><c>ValidateResolvedTenant = false</c> 时强制只保留主体贡献者。</summary>
/// <remarks>
/// 注册表校验关闭后，请求头、查询串和子域名均为未经验证的来源；须在所有 Configure 之后收窄解析链。
/// 分别覆盖默认装配、宿主前置注册和后置追加，避免注册顺序绕过安全边界。
/// </remarks>
public class PrincipalOnlyEnforcementTests
{
    [Fact]
    public void Default_assembly_keeps_only_the_principal_contributor()
    {
        var contributors = ResolveChain(services =>
            services.AddMultiTenancy(options => options.ValidateResolvedTenant = false));

        Assert.Single(contributors);
        Assert.IsType<CurrentPrincipalTenantResolveContributor>(contributors[0]);
    }

    /// <summary>
    /// 宿主在 <c>AddMultiTenancy</c> 之前注册贡献者：链非空使默认装配跳过，
    /// 只在装配默认链时收窄的话，这些未验证来源会原样留在链里。
    /// </summary>
    [Fact]
    public void Host_registered_contributors_before_setup_are_removed()
    {
        var contributors = ResolveChain(services =>
        {
            services.Configure<TenantResolveOptions>(options =>
            {
                options.Contributors.Add(new HeaderTenantResolveContributor());
                options.Contributors.Add(new QueryStringTenantResolveContributor());
            });
            services.AddMultiTenancy(options => options.ValidateResolvedTenant = false);
        });

        Assert.Single(contributors);
        Assert.IsType<CurrentPrincipalTenantResolveContributor>(contributors[0]);
    }

    // 后置 Configure 也必须被 PostConfigure 收窄，不能重新引入未经验证的来源。
    [Fact]
    public void Host_appended_contributors_after_setup_are_removed()
    {
        var contributors = ResolveChain(services =>
        {
            services.AddMultiTenancy(options => options.ValidateResolvedTenant = false);
            services.Configure<TenantResolveOptions>(options =>
            {
                options.Contributors.Add(new DomainTenantResolveContributor());
                options.Contributors.Add(new HeaderTenantResolveContributor());
            });
        });

        Assert.Single(contributors);
        Assert.IsType<CurrentPrincipalTenantResolveContributor>(contributors[0]);
    }

    /// <summary>
    /// 校验开启（默认）时不动链——那种形态下未经验证的来源仍会经注册表校验，
    /// 收口反而会砍掉子域名与请求头这两条合法路径。
    /// </summary>
    [Fact]
    public void Validation_enabled_keeps_the_full_chain()
    {
        // 开着注册表校验就必须有 ITenantStore，否则 TenantStoreRegistrationValidator 拦下——
        // 这里注册配置型 store，等价于一个合法的 Identity 形态宿主
        var contributors = ResolveChain(services =>
        {
            services.AddMultiTenancy();
            services.AddInMemoryTenantStore(_ => { });
        });

        Assert.Equal(4, contributors.Count);
        Assert.IsType<CurrentPrincipalTenantResolveContributor>(contributors[0]);
    }

    /// <summary>开着注册表校验却没注册 <c>ITenantStore</c>：必须在读取选项时就失败，不能留到运行期。</summary>
    /// <remarks>
    /// 若这个前提只在中间件里发现，每个带租户的请求都会失败一次、而进程"健康"地跑着。
    /// </remarks>
    [Fact]
    public void Validation_enabled_without_a_tenant_store_fails_fast()
    {
        var error = Assert.Throws<OptionsValidationException>(
            () => ResolveChain(services => services.AddMultiTenancy()));

        Assert.Contains("no ITenantStore is registered", error.Message, StringComparison.Ordinal);
    }

    // 改了配置节路径，报错里的键名也得跟着走：照着默认节名去改开关，实际读取的配置不会变
    [Fact]
    public void The_missing_store_failure_names_the_configured_section()
    {
        var error = Assert.Throws<OptionsValidationException>(
            () => ResolveChain(services => services.AddMultiTenancy(configSectionPath: "Web:Tenants")));

        Assert.Contains("Web:Tenants:ValidateResolvedTenant", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MultiTenancyOptions.SectionName, error.Message, StringComparison.Ordinal);
    }

    private static IList<ITenantResolveContributor> ResolveChain(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // 真实宿主总有 IConfiguration：AddMultiTenancy 绑定 Leistd:MultiTenancy
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        configure(services);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TenantResolveOptions>>().Value.Contributors;
    }
}
