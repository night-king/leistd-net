using Leistd.Lock.Memory;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Registration;
using Leistd.Settings.Stores;
using Leistd.Settings.Tests.TestDoubles;
using Leistd.TestBase.Doubles;
using Leistd.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.Settings.Tests.Core;

/// <summary>默认写入入口缺锁或缺工作单元时，宿主启动即失败；宿主替换了写入入口时不强加这两项。</summary>
/// <remarks>
/// 用 Production 环境：默认 Host 只在 Development 下做构建期依赖校验，生产宿主缺依赖原本要到第一次写入才失败。
/// </remarks>
public sealed class SettingManagerDependencyCheckTests
{
    [Theory]
    [InlineData("lock", "IDistributedLock", "AddMemoryLocalLock()")]
    [InlineData("unit-of-work", "IUnitOfWorkManager", "AddUnitOfWork()")]
    public async Task A_production_host_missing_a_write_dependency_fails_to_start(string missing, string service, string registration)
    {
        using var host = Build(services =>
        {
            if (missing != "lock")
            {
                services.AddMemoryLocalLock();
            }

            if (missing != "unit-of-work")
            {
                services.AddSingleton<IUnitOfWorkManager>(new RecordingUnitOfWorkManager());
            }
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(service, error.Message, StringComparison.Ordinal);
        Assert.Contains(registration, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_both_dependencies_starts()
    {
        using var host = Build(services => services
            .AddMemoryLocalLock()
            .AddSingleton<IUnitOfWorkManager>(new RecordingUnitOfWorkManager()));

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>替换了写入入口的宿主自己决定串行与事务，没有锁与工作单元也能启动。</summary>
    [Fact]
    public async Task A_host_that_replaces_the_setting_manager_starts_without_them()
    {
        using var host = Build(services => services.Replace(ServiceDescriptor.Transient<ISettingManager, ReplacementManager>()));

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public void Repeated_registration_adds_the_check_once()
    {
        var services = new ServiceCollection().AddSettingsCore().AddSettingsCore();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
                                              && descriptor.ImplementationType == typeof(SettingManagerDependencyCheck));
    }

    private static IHost Build(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });
        builder.Services
            .AddSettingsCore()
            .AddSettingsCore()
            .AddSingleton<ISettingStore>(new FakeSettingStore())
            .AddSingleton<ICurrentUser>(new FakeCurrentUser())
            .AddSingleton<ISettingDefinitionProvider, Definitions>();
        configure(builder.Services);
        return builder.Build();
    }

    private sealed class Definitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context) => context.Add("Display.Language", "zh-CN");
    }

    private sealed class ReplacementManager : ISettingManager
    {
        public Task SetAsync(
            string name,
            string? value,
            SettingScopes scope,
            string? userId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
