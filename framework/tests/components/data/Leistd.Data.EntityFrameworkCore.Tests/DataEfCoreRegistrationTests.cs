using Leistd.Data.EntityFrameworkCore.Querying;
using Leistd.Data.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Data.EntityFrameworkCore.Tests;

public sealed class DataEfCoreRegistrationTests
{
    [Fact]
    public void Standalone_registration_is_idempotent_and_preserves_a_host_executer()
    {
        var services = new ServiceCollection().AddDataEfCore().AddDataEfCore();
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IQueryableAsyncExecuter));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<EfCoreQueryableAsyncExecuter>(provider.GetRequiredService<IQueryableAsyncExecuter>());

        var host = new EfCoreQueryableAsyncExecuter();
        var custom = new ServiceCollection();
        custom.AddSingleton<IQueryableAsyncExecuter>(host);
        custom.AddDataEfCore().AddDataEfCore();
        using var customProvider = custom.BuildServiceProvider();
        Assert.Same(host, customProvider.GetRequiredService<IQueryableAsyncExecuter>());
    }
}
