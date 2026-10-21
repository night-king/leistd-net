using Leistd.BackgroundJobs.InProcess.Options;
using Leistd.BackgroundJobs.InProcess.Queues;
using Leistd.BackgroundJobs.InProcess.Recurring;
using Leistd.BackgroundJobs.Options;
using Leistd.BackgroundJobs.Queues;
using Leistd.BackgroundJobs.Recurring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Leistd.BackgroundJobs.InProcess;

/// <summary>进程内后台作业实现的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册进程内调度器与后台任务队列。</summary>
    /// <remarks>
    /// <para>调度器执行所有经 <c>AddRecurringJob&lt;TJob&gt;</c> 登记的任务；登记与本方法的调用顺序无关。
    /// 选项绑定 <paramref name="configSectionPath"/>（默认 <c>Leistd:BackgroundJobs</c>）与其下的 <c>InProcess</c> 子节，
    /// 重复调用换用另一配置节时抛出 <see cref="InvalidOperationException"/>。</para>
    /// <para>集群任务需要 <c>IDistributedLock</c>，缺失时宿主启动失败。水位默认存在进程内，
    /// 多副本部署再注册共享存储的实现（如 <c>AddBackgroundJobsEfCore&lt;TDbContext&gt;()</c>）。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddRedisDistributedLock(redisConnectionString);
    /// builder.Services.AddInProcessBackgroundJobs();
    /// builder.Services.AddRecurringJob&lt;LeadRecycleJob&gt;(
    ///     "crm.lead-recycle", RecurringJobSchedule.DailyAt(new TimeOnly(18, 0)), RecurringJobScope.Cluster);
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">在配置节之后应用的选项配置。</param>
    /// <param name="configSectionPath">通用选项的配置节；进程内调优参数取其下的 <c>InProcess</c> 子节。</param>
    public static IServiceCollection AddInProcessBackgroundJobs(
        this IServiceCollection services,
        Action<BackgroundJobOptions>? configure = null,
        string configSectionPath = BackgroundJobOptions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        var inProcessSectionPath = $"{configSectionPath}:{InProcessBackgroundJobOptions.SubsectionName}";

        // 选项只有一份：换用另一配置节的重复调用会让校验消息报错键名
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<InProcessBackgroundJobOptionsValidator>().FirstOrDefault()
                is { } registered && registered.ConfigSectionPath != inProcessSectionPath)
        {
            throw new InvalidOperationException(
                $"AddInProcessBackgroundJobs() already binds '{registered.ConfigSectionPath}'; it cannot also bind '{inProcessSectionPath}'.");
        }

        services.AddOptions<BackgroundJobOptions>().BindConfiguration(configSectionPath);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InProcessBackgroundJobOptions>>(
            new InProcessBackgroundJobOptionsValidator(inProcessSectionPath)));
        services.AddOptions<InProcessBackgroundJobOptions>()
            .BindConfiguration(inProcessSectionPath)
            .ValidateOnStart();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRecurringJobStateStore, InMemoryRecurringJobStateStore>();

        // 幂等：组合根拆分时重复调用是常态，托管服务注册两遍会让每个任务每个时段跑两次
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RecurringJobRunner)))
        {
            return services;
        }

        services.AddSingleton<RecurringJobRunner>();
        services.AddSingleton<RecurringJobPlan>();
        // 可替换的单实现：宿主先登记或之后 Replace 都能覆盖
        services.TryAddSingleton<IRecurringJobStatusReader, RecurringJobStatusReader>();
        services.AddHostedService<RecurringJobScheduler>();
        services.TryAddSingleton<RecurringJobSchedulerMarker>();

        // 队列与消费者必须是同一个通道：两处各注册一个实例，生产者写进一个、消费者读另一个，工作项永不执行
        services.AddSingleton<BackgroundTaskQueue>();
        services.AddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<BackgroundTaskQueue>());
        services.AddHostedService<BackgroundTaskQueueWorker>();
        return services;
    }
}
