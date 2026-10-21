using Leistd.MultiTenancy.AspNetCore;
using Leistd.MultiTenancy.AspNetCore.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary>
/// 传了委托也绑定配置节：委托重载若不读 <c>Leistd:MultiTenancy</c>，宿主只想改一项时，
/// 配置文件里的其余项会被静默忽略。
/// </summary>
public class MultiTenancyOptionsBindingTests
{
    [Fact]
    public void The_section_is_bound_and_the_delegate_overrides_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Leistd:MultiTenancy:HeaderName"] = "X-Org",
                ["Leistd:MultiTenancy:ValidateResolvedTenant"] = "true"
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddMultiTenancy(options => options.ValidateResolvedTenant = false)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;

        Assert.Equal("X-Org", options.HeaderName);
        Assert.False(options.ValidateResolvedTenant);
    }
}
