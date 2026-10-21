using Leistd.DependencyInjection.DynamicProxy.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>宿主漏接代理工厂时，工作单元在启动时失败，而不是照常运行、静默失去织入。</summary>
/// <remarks>
/// 用真实的 <c>HostApplicationBuilder</c>：检查依赖两件官方行为——<c>ConfigureContainer</c> 会调用工厂的
/// <c>CreateBuilder</c>（标记在那里登记），以及所有 <c>StartingAsync</c> 先于任何 <c>StartAsync</c>。
/// 手工模拟调用顺序验证不到这两点。
/// </remarks>
public sealed class UnitOfWorkWeavingCheckTests
{
    [Fact]
    public async Task Host_without_the_dynamic_proxy_factory_fails_before_any_hosted_service_starts()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddUnitOfWork();
        // 注册在检查之前的托管服务：检查放在 StartAsync 时它会先跑完
        var earlier = new RecordingHostedService();
        builder.Services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(earlier));
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("ConfigureContainer", error.Message);
        Assert.False(earlier.Started);
    }

    [Fact]
    public async Task Host_with_the_dynamic_proxy_factory_starts()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.ConfigureContainer(new DynamicProxyServiceRegistrationCallbackFactory());
        builder.Services.AddUnitOfWork();
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    private sealed class RecordingHostedService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
