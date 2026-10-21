using Leistd.BackgroundJobs.EntityFrameworkCore.EntityConfigurations;
using Leistd.BackgroundJobs.EntityFrameworkCore.Entities;
using Leistd.BackgroundJobs.EntityFrameworkCore.Stores;
using Leistd.BackgroundJobs.Recurring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.DependencyInjection.Extensions;

namespace Leistd.BackgroundJobs.EntityFrameworkCore;

/// <summary>周期任务水位的 EF Core 存储注册与模型配置。</summary>
public static class DependencyInjection
{
    /// <summary>用指定 DbContext 存储集群周期任务的完成水位，替换进程内默认实现。</summary>
    /// <remarks>
    /// 多副本部署必须注册。与调度器注册的先后无关，本方法替换进程内默认实现；同一上下文重复调用幂等，
    /// 已注册其他水位存储（含另一 DbContext 的 EF 存储）时抛出 <see cref="InvalidOperationException"/>。宿主须已注册 <c>AddUnitOfWork()</c> 与
    /// <c>AddUnitOfWorkEfCore()</c>，并在 <c>OnModelCreating</c> 里调用 <see cref="ConfigureBackgroundJobs"/>。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddBackgroundJobsEfCore&lt;AppDbContext&gt;();
    ///
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ConfigureBackgroundJobs();
    /// }
    /// </code>
    /// </example>
    /// <typeparam name="TDbContext">承载水位表的 DbContext。</typeparam>
    public static IServiceCollection AddBackgroundJobsEfCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        // 进程内水位只是调度器自带的兜底，只移除它；其余实现与另一上下文的存储都算冲突
        foreach (var fallback in services.Where(descriptor => descriptor.ServiceType == typeof(IRecurringJobStateStore)
                     && descriptor.ImplementationType is { } type
                     && typeof(IProcessLocalRecurringJobStateStore).IsAssignableFrom(type)).ToList())
        {
            services.Remove(fallback);
        }

        services.EnsureSingleAuthoritative<IRecurringJobStateStore, EfCoreRecurringJobStateStore<TDbContext>>(
            ServiceLifetime.Transient,
            "Recurring job watermarks have a single shared store; map them in one DbContext.");
        services.TryAddTransient<IRecurringJobStateStore, EfCoreRecurringJobStateStore<TDbContext>>();
        return services;
    }

    /// <summary>映射 <see cref="RecurringJobState"/>，在 OnModelCreating 中调用。</summary>
    public static ModelBuilder ConfigureBackgroundJobs(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new RecurringJobStateConfiguration());
        return modelBuilder;
    }
}
