using Leistd.BackgroundJobs;
using Leistd.BackgroundJobs.Recurring;
using Leistd.DependencyInjection.Extensions;
using Leistd.OperationRecords.EntityFrameworkCore.Options;
using Leistd.OperationRecords.EntityFrameworkCore.Retention;
using Microsoft.Extensions.Options;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.EntityFrameworkCore.EntityConfigurations;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.EntityFrameworkCore.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.OperationRecords.EntityFrameworkCore;

/// <summary>操作记录 EF Core 持久化的注册与模型配置。</summary>
public static class DependencyInjection
{
    /// <summary>注册 EF Core 操作记录存储（基于指定 DbContext），同时提供历史读取与查询用例。</summary>
    /// <remarks>
    /// <para>登记 <see cref="IOperationRecordWriter"/> 与 <see cref="IOperationRecordReader"/>，并调用
    /// <c>AddOperationRecordQueries()</c>；HTTP 端点与保留期归档仍由宿主显式映射与注册。</para>
    /// <para>本存储通过 <c>IDbContextProvider&lt;TDbContext&gt;</c> 获取绑定连接的上下文，
    /// 因此宿主须注册 <c>AddUnitOfWork()</c> 与 <c>AddUnitOfWorkEfCore()</c>。</para>
    /// <para>记录器另需四样由宿主注册的前置，缺一个在首次解析 <c>IOperationRecorder</c> 时才暴露：
    /// <c>IClock</c>（<c>AddSingleton&lt;IClock, UtcClockProvider&gt;()</c>）、<c>ICurrentTenant</c>（<c>AddMultiTenancyCore()</c>）、
    /// <c>ICurrentUser</c>（<c>AddAmbientContext()</c>）、<c>ICorrelationIdProvider</c>（<c>AddCorrelationIdCore()</c>）。</para>
    /// <para>同一 DbContext 重复调用幂等；已用另一 DbContext 或日志写出注册过记录写入方时抛出 <see cref="InvalidOperationException"/>。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddSingleton&lt;IClock, UtcClockProvider&gt;();
    /// builder.Services.AddMultiTenancyCore();
    /// builder.Services.AddAmbientContext();
    /// builder.Services.AddCorrelationIdCore();
    /// builder.Services.AddOperationRecordsEfCore&lt;AppDbContext&gt;();
    ///
    /// // DbContext 里映射操作记录表
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     base.OnModelCreating(modelBuilder);
    ///     modelBuilder.ConfigureOperationRecords();
    /// }
    /// </code>
    /// </example>
    /// <typeparam name="TDbContext">承载操作记录表的 DbContext。</typeparam>
    public static IServiceCollection AddOperationRecordsEfCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        // 操作记录只有一个权威存储：两个上下文各注册一次时会静默取其一
        services.EnsureSingleAuthoritative<IOperationRecordWriter, EfCoreOperationRecordStore<TDbContext>>(
            ServiceLifetime.Transient,
            "Operation records have a single authoritative store; map OperationRecord in one DbContext and do not combine it with another writer.");
        services.EnsureSingleAuthoritative<IOperationRecordReader, EfCoreOperationRecordStore<TDbContext>>(
            ServiceLifetime.Transient,
            "Operation record history is read from the same store it is written to.");

        services.AddOperationRecordQueries();
        services.TryAddTransient<IOperationRecordWriter, EfCoreOperationRecordStore<TDbContext>>();
        services.TryAddTransient<IOperationRecordReader, EfCoreOperationRecordStore<TDbContext>>();

        return services;
    }

    /// <summary>启用保留期归档：到期记录按天搬入归档表，作为集群周期任务执行。</summary>
    /// <remarks>
    /// <para>选项绑定 <paramref name="configSectionPath"/>（默认 <c>Leistd:OperationRecords:Retention</c>）并在启动期校验，重复调用换用另一配置节时抛出 <see cref="InvalidOperationException"/>；默认 <c>Enabled = false</c>，
    /// 任务照常排期、到点跳过，打开开关下一轮即生效。</para>
    /// <para>需要后台作业调度器（如 <c>AddInProcessBackgroundJobs()</c>）与分布式锁；
    /// 归档按物理库逐个执行，独立库租户的记录在各自的库里归档。</para>
    /// <para>逐库遍历（<c>ITenantDatabaseRunner</c>）由 <c>AddMultiTenancyCore()</c> 提供；不分库时只有宿主库。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddOperationRecordsEfCore&lt;AppDbContext&gt;();   // 连同上面那四样前置
    /// builder.Services.AddOperationRecordRetention&lt;AppDbContext&gt;();
    /// </code>
    /// </example>
    /// <typeparam name="TDbContext">承载操作记录与归档表的 DbContext。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">在配置节之后应用的选项配置。</param>
    /// <param name="configSectionPath">选项绑定的配置节，校验消息按它报键名。</param>
    public static IServiceCollection AddOperationRecordRetention<TDbContext>(
        this IServiceCollection services,
        Action<OperationRecordRetentionOptions>? configure = null,
        string configSectionPath = OperationRecordRetentionOptions.SectionName)
        where TDbContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        // 选项只有一份：换用另一配置节的重复调用会让校验消息报错键名
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<OperationRecordRetentionOptionsValidator>().FirstOrDefault()
                is { } registered && registered.ConfigSectionPath != configSectionPath)
        {
            throw new InvalidOperationException(
                $"AddOperationRecordRetention() already binds '{registered.ConfigSectionPath}'; it cannot also bind '{configSectionPath}'.");
        }

        services.AddOptions<OperationRecordRetentionOptions>()
            .BindConfiguration(configSectionPath)
            .ValidateOnStart();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OperationRecordRetentionOptions>>(new OperationRecordRetentionOptionsValidator(configSectionPath)));
        services.TryAddTransient<IOperationRecordArchiveService, OperationRecordArchiveService<TDbContext>>();
        services.AddRecurringJob<OperationRecordArchiveJob>(
            OperationRecordArchiveJob.Name,
            sp => RecurringJobSchedule.DailyAt(new TimeOnly(
                sp.GetRequiredService<IOptions<OperationRecordRetentionOptions>>().Value.DailyRunHourUtc, 0)),
            RecurringJobScope.Cluster,
            isEnabled: sp => sp.GetRequiredService<IOptionsSnapshot<OperationRecordRetentionOptions>>().Value.Enabled);
        return services;
    }

    /// <summary>将 <see cref="OperationRecord"/> 与 <see cref="OperationRecordArchive"/> 的实体配置应用到 DbContext。在 OnModelCreating 中调用。</summary>
    /// <remarks>归档表随原表一起映射，启用保留期不需要改模型。</remarks>
    public static ModelBuilder ConfigureOperationRecords(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OperationRecordConfiguration());
        modelBuilder.ApplyConfiguration(new OperationRecordArchiveConfiguration());
        return modelBuilder;
    }
}
