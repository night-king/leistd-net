using Leistd.UnitOfWork.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.DependencyInjection.DynamicProxy.Registration;

namespace Leistd.UnitOfWork.Tests.Core;

public sealed class UnitOfWorkDynamicProxyTests
{
    [Fact]
    public async Task Async_unit_of_work_interceptor_is_adapted_to_castle_proxy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddTransient<IExampleService, ExampleService>();

        await using var provider = (ServiceProvider)new DynamicProxyServiceRegistrationCallbackFactory()
            .CreateServiceProvider(services);
        var service = provider.GetRequiredService<IExampleService>();

        Assert.Equal(42, await service.ExecuteAsync());
    }

    [Fact]
    public async Task Disabled_attribute_skips_the_unit_of_work()
    {
        var service = BuildService();

        Assert.True(await service.ExecuteDisabledAsync());
    }

    [Fact]
    public async Task Non_transactional_attribute_still_creates_a_unit_of_work()
    {
        var service = BuildService();

        Assert.False(await service.ExecuteNonTransactionalAsync());
    }

    private static IExampleService BuildService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddTransient<IExampleService, ExampleService>();

        var provider = new DynamicProxyServiceRegistrationCallbackFactory()
            .CreateServiceProvider(services);
        return provider.GetRequiredService<IExampleService>();
    }

    public interface IExampleService
    {
        Task<int> ExecuteAsync();

        Task<bool> ExecuteDisabledAsync();

        Task<bool> ExecuteNonTransactionalAsync();
    }

    public sealed class ExampleService(IUnitOfWorkManager unitOfWorkManager) : IExampleService
    {
        [UnitOfWork]
        public Task<int> ExecuteAsync() => Task.FromResult(42);

        [UnitOfWork(IsDisabled = true)]
        public Task<bool> ExecuteDisabledAsync() =>
            Task.FromResult(unitOfWorkManager.Current is null);

        [UnitOfWork(false)]
        public Task<bool> ExecuteNonTransactionalAsync() =>
            Task.FromResult(unitOfWorkManager.Current!.Options.IsTransactional);
    }
}
