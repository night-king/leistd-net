using Microsoft.Extensions.Options;
using Leistd.Notifications.EntityFrameworkCore.Retention;
using Leistd.Notifications.EntityFrameworkCore.Options;
using Leistd.BackgroundJobs.Recurring;
using Leistd.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Leistd.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Notifications.EntityFrameworkCore.EntityConfigurations;
using Leistd.Notifications.EntityFrameworkCore.Stores;
using Leistd.Notifications.Stores;

namespace Leistd.Notifications.EntityFrameworkCore;

/// <summary>通知 EF Core 持久化的注册与模型配置。</summary>
public static class DependencyInjection
{
    /// <summary>注册 EF Core 通知持久化存储（基于指定 DbContext）。</summary>
    /// <remarks>
    /// <para>本存储通过 <c>IDbContextProvider&lt;TDbContext&gt;</c> 获取绑定连接的上下文，
    /// 因此宿主须注册 <c>AddUnitOfWork()</c> 与 <c>AddUnitOfWorkEfCore()</c>。</para>
    /// <para>同时调用 Core 的 <c>AddNotifications()</c>（幂等），只要通知历史、不要实时推送时也能解析 <c>INotificationPublisher</c>。</para>
    /// <para>还需要 <c>IClock</c>：宿主自行 <c>AddSingleton&lt;IClock, UtcClockProvider&gt;()</c>（DDD 基础设施包已代为注册）。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddSingleton&lt;IClock, UtcClockProvider&gt;();
    /// builder.Services.AddNotificationsEfCore&lt;AppDbContext&gt;();
    ///
    /// // DbContext 里映射通知表
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     base.OnModelCreating(modelBuilder);
    ///     modelBuilder.ConfigureNotifications();
    /// }
    /// </code>
    /// </example>
    public static IServiceCollection AddNotificationsEfCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {

        // 发布器只消费一个权威存储：两个上下文各注册一次时会静默取其一
        services.EnsureSingleAuthoritative<INotificationStore, EfCoreNotificationStore<TDbContext>>(
            ServiceLifetime.Transient,
            "Notifications have a single authoritative store; map NotificationRecord in one DbContext.");

        // 同家族内组合：只装持久化包也能解析发布器；跨组件前置仍由宿主显式组合
        services.AddNotifications();
        services.TryAddTransient<INotificationStore, EfCoreNotificationStore<TDbContext>>();
        return services;
    }

    /// <summary>启用通知保留期：到期通知每天按物理库逐个删除，作为集群周期任务执行。</summary>
    /// <remarks>
    /// <para>选项绑定 <paramref name="configSectionPath"/>（默认 <c>Leistd:Notifications:Retention</c>）并在启动期校验，重复调用换用另一配置节时抛出 <see cref="InvalidOperationException"/>；默认开启，已读保留 90 天、未读保留 365 天，
    /// 均按创建时间计。开关与天数每轮取当前值，执行时刻只在排期时取一次。</para>
    /// <para>需要后台作业调度器（如 <c>AddInProcessBackgroundJobs()</c>）与分布式锁。</para>
    /// <para>还需要 <c>AddMultiTenancyCore()</c>，单库项目也要：逐库遍历（<c>ITenantDatabaseRunner</c>）由它提供，不分库时只有宿主库。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddMultiTenancyCore();            // 提供逐库遍历；单库时清单只有宿主库
    /// builder.Services.AddNotificationsEfCore&lt;AppDbContext&gt;();
    /// builder.Services.AddNotificationRetention&lt;AppDbContext&gt;();
    /// </code>
    /// </example>
    /// <typeparam name="TDbContext">承载通知表的 DbContext。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">在配置节之后应用的选项配置。</param>
    /// <param name="configSectionPath">选项绑定的配置节，校验消息按它报键名。</param>
    public static IServiceCollection AddNotificationRetention<TDbContext>(
        this IServiceCollection services,
        Action<NotificationRetentionOptions>? configure = null,
        string configSectionPath = NotificationRetentionOptions.SectionName)
        where TDbContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        // 选项只有一份：换用另一配置节的重复调用会让校验消息报错键名
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<NotificationRetentionOptionsValidator>().FirstOrDefault()
                is { } registered && registered.ConfigSectionPath != configSectionPath)
        {
            throw new InvalidOperationException(
                $"AddNotificationRetention() already binds '{registered.ConfigSectionPath}'; it cannot also bind '{configSectionPath}'.");
        }

        services.AddOptions<NotificationRetentionOptions>()
            .BindConfiguration(configSectionPath)
            .ValidateOnStart();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<NotificationRetentionOptions>>(new NotificationRetentionOptionsValidator(configSectionPath)));
        services.AddRecurringJob<NotificationRetentionJob<TDbContext>>(
            NotificationRetentionJob<TDbContext>.Name,
            sp => RecurringJobSchedule.DailyAt(new TimeOnly(
                sp.GetRequiredService<IOptions<NotificationRetentionOptions>>().Value.DailyRunHourUtc, 0)),
            RecurringJobScope.Cluster,
            isEnabled: sp => sp.GetRequiredService<IOptionsSnapshot<NotificationRetentionOptions>>().Value.Enabled);
        return services;
    }

    /// <summary>映射 <c>NotificationRecord</c>，在 OnModelCreating 中调用。</summary>
    public static ModelBuilder ConfigureNotifications(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new NotificationRecordConfiguration());
        return modelBuilder;
    }

}
