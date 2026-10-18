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

    /// <summary>工厂登记看不出实现类型时才解析一次；只支持异步释放的替换实现能正常启动，并在检查结束时被释放。</summary>
    [Fact]
    public async Task An_opaque_replacement_that_only_disposes_asynchronously_starts_and_is_released()
    {
        var manager = new AsyncOnlyManager();
        using var host = Build(services => services.Replace(ServiceDescriptor.Transient<ISettingManager>(_ => manager)));

        await host.StartAsync();
        await host.StopAsync();

        Assert.True(manager.Disposed);
    }

    /// <summary>按类型登记的替换实现不在启动时解析：它自己缺的依赖照旧在首次使用时报出，不被说成默认实现缺锁。</summary>
    [Fact]
    public async Task A_replacement_registered_by_type_is_not_resolved_at_startup()
    {
        using var host = Build(services => services.Replace(ServiceDescriptor.Transient<ISettingManager, ManagerNeedingUnregisteredService>()));

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>工厂登记解析失败时原样上抛它自己的原因，不推断成默认实现缺锁或缺工作单元。</summary>
    [Fact]
    public async Task A_failing_opaque_registration_keeps_its_own_error()
    {
        var failure = new InvalidOperationException("replacement factory failed");
        using var host = Build(services => services.Replace(ServiceDescriptor.Transient<ISettingManager>(_ => throw failure)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Same(failure, error);
    }

    [Fact]
    public void Repeated_registration_adds_the_check_once()
    {
        using var provider = new ServiceCollection().AddSettingsCore().AddSettingsCore().BuildServiceProvider();

        Assert.Single(provider.GetServices<IHostedService>().OfType<SettingManagerDependencyCheck>());
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

    private sealed class AsyncOnlyManager : ISettingManager, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task SetAsync(
            string name,
            string? value,
            SettingScopes scope,
            string? userId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    // 依赖一个宿主没有登记的服务
    private sealed class ManagerNeedingUnregisteredService(IUnregisteredService dependency) : ISettingManager
    {
        public Task SetAsync(
            string name,
            string? value,
            SettingScopes scope,
            string? userId = null,
            CancellationToken cancellationToken = default)
            => dependency.RunAsync();
    }

    private interface IUnregisteredService
    {
        Task RunAsync();
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
