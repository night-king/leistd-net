using Leistd.BackgroundJobs.Recurring;
using Leistd.EventBus.EventHandlers;
using Leistd.Settings.Definitions;
using Leistd.Settings.Events;
using Leistd.Settings.Hosting.Configuration;
using Leistd.Settings.Hosting.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.Hosting.Runtime;

// 其它实例跟上：设置行是共享的，进程内配置不是。每个副本按周期重读一次宿主行。
internal sealed class HostSettingRefreshJob(HostSettingApplier applier) : IRecurringJob
{
    public const string Name = "settings.host-refresh";

    public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
        => applier.ApplyAsync(cancellationToken);
}

// 本进程立即生效：在写入所在事务提交后执行，回滚的写入不会被用上。
// 应用失败只记告警（设置已落库），下一轮周期刷新会再应用。
internal sealed class HostSettingChangedHandler(
    HostSettingApplier applier,
    IOptions<HostSettingBindingCollection> bindings,
    ILogger<HostSettingChangedHandler> logger) : IEventHandler<SettingChangedEvent>
{
    public async Task HandleAsync(SettingChangedEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.Scope != SettingScopes.Host
            || bindings.Value.Bindings.All(binding => binding.SettingName != @event.Name))
        {
            return;
        }

        try
        {
            await applier.ApplyAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Applying host setting {SettingName} failed; the next refresh will retry.", @event.Name);
        }
    }
}

// 开始接收请求之前加载库里的值，最早的请求与日志即使用设置值。
// 库可能尚未迁移（全新部署首次启动），失败只记录不抛，沿用部署配置。
internal sealed class HostSettingStartupService(
    HostSettingsConfigurationProvider configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<HostSettingStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.IsAttached)
        {
            throw new InvalidOperationException(
                "Host settings are registered but not attached to the configuration. "
                + "Call UseHostSettings() on the built host before it starts.");
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<HostSettingApplier>().ApplyAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Applying host settings at startup failed; the deployment configuration stays in effect until the next refresh.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
