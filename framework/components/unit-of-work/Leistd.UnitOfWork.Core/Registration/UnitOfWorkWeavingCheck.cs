using Leistd.DependencyInjection.DynamicProxy.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Leistd.UnitOfWork.Registration;

// 宿主漏接代理工厂时 [UnitOfWork] 不生效、事件处理器两个阶段各执行一次，且不报错；启动期在此拒绝。
// 放在 StartingAsync：先于所有托管服务的 StartAsync。不经 Host 启动的场景不在检查范围内。
internal sealed class UnitOfWorkWeavingCheck(IServiceProvider serviceProvider) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (serviceProvider.GetService<DynamicProxyWeavingMarker>() is null)
        {
            throw new InvalidOperationException(
                "The unit of work requires interceptor weaving, but the host does not use the DynamicProxy " +
                "service provider factory. Register new DynamicProxyServiceRegistrationCallbackFactory() via " +
                "builder.Host.UseServiceProviderFactory(...) (WebApplicationBuilder), " +
                "hostBuilder.UseServiceProviderFactory(...) (IHostBuilder) or " +
                "builder.ConfigureContainer(...) (HostApplicationBuilder) before building the host.");
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
