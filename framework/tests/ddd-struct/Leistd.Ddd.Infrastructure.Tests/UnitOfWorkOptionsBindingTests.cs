using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary><c>AddDddInfrastructure</c> 的工作单元选项：绑定 <c>Leistd:UnitOfWork</c>，委托在其后应用。</summary>
public class UnitOfWorkOptionsBindingTests
{
    // 走委托重载注册时配置节同样要绑定，否则 appsettings 里写的超时静默不生效
    [Fact]
    public void The_section_is_bound_and_the_delegate_overrides_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Leistd:UnitOfWork:Timeout"] = "00:00:30",
                ["Leistd:UnitOfWork:IsTransactional"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDddInfrastructure(options => options.IsTransactional = true);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<UnitOfWorkOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.True(options.IsTransactional);
    }
}
