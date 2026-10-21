using Leistd.OperationRecords.Logging.Constants;
using Leistd.OperationRecords.Logging.Recording;
using Leistd.EventBus.Abstractions;
using Leistd.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Leistd.OperationRecords.Logging.Registration;

// 日志模式下类别被过滤会让记录静默消失：启动期校验日志级别与提交后输出所需的两个组件
internal sealed class OperationRecordLoggingStartupCheck(
    IServiceProvider serviceProvider,
    OperationRecordLogEmitter emitter) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!emitter.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Operation records are written to the log category '{OperationRecordLogging.CategoryName}', "
                + "but it is not enabled at Information; security records would be silently dropped. "
                + $"Set Logging:LogLevel:{OperationRecordLogging.CategoryName} (or the equivalent provider override) to Information.");
        }

        var probe = serviceProvider.GetService<IServiceProviderIsService>();
        if (probe is not null
            && (!probe.IsService(typeof(IUnitOfWorkManager)) || !probe.IsService(typeof(ILocalEventDispatcher))))
        {
            throw new InvalidOperationException(
                "The operation record log writer defers succeeded records until the unit of work commits; "
                + "register AddUnitOfWork() and AddLocalEventBus().");
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
