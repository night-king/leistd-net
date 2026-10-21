using Leistd.OperationRecords.Logging.Constants;
using Leistd.OperationRecords.Logging.EventHandlers;
using Leistd.OperationRecords.Logging.Events;
using Leistd.OperationRecords.Logging.Recording;
using Leistd.OperationRecords.Logging.Registration;
using Leistd.OperationRecords.Logging.Stores;
using Leistd.DependencyInjection.Extensions;
using Leistd.EventBus.EventHandlers;
using Leistd.OperationRecords.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Leistd.OperationRecords.Logging;

/// <summary>操作记录结构化日志输出的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>把操作记录写成结构化日志（<see cref="OperationRecordLogging.CategoryName"/> 类别），不保存可回读的历史。</summary>
    /// <remarks>
    /// <para>记录器与动作定义与数据库存储相同，只换写出的去处。不注册历史读取与查询：
    /// <c>MapOperationRecords</c> 在本模式下映射即报错，也没有保留期归档。</para>
    /// <para>前置：<c>AddUnitOfWork()</c> 与 <c>AddLocalEventBus()</c>（成功记录借工作单元的提交后阶段写出，
    /// 宿主须启用拦截器织入），以及记录器需要的 <c>IClock</c>、<c>ICurrentTenant</c>、<c>ICurrentUser</c>、
    /// <c>ICorrelationIdProvider</c>。日志类别在启动期须对 Information 开启，否则宿主启动失败。</para>
    /// <para>与数据库存储互斥：一个宿主只有一个记录写入方。重复调用幂等；已注册数据库存储时抛出 <see cref="InvalidOperationException"/>。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddUnitOfWork();
    /// builder.Services.AddLocalEventBus();
    /// builder.Services.AddOperationRecordsLogging();
    /// </code>
    /// </example>
    public static IServiceCollection AddOperationRecordsLogging(this IServiceCollection services)
    {
        services.EnsureSingleAuthoritative<IOperationRecordWriter, LoggingOperationRecordWriter>(
            ServiceLifetime.Transient,
            "Operation records have a single authoritative writer; do not combine the log writer with a storage adapter.");

        services.AddOperationRecords();
        services.TryAddSingleton<OperationRecordLogEmitter>();
        services.TryAddTransient<IOperationRecordWriter, LoggingOperationRecordWriter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IEventHandler<OperationRecordCommitted>, OperationRecordCommittedLogHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OperationRecordLoggingStartupCheck>());
        return services;
    }
}
