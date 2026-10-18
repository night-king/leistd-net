using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Leistd.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Settings.Stores;
using Leistd.Settings.EntityFrameworkCore.EntityConfigurations;
using Leistd.Settings.EntityFrameworkCore.Entities;
using Leistd.Settings.EntityFrameworkCore.Stores;

namespace Leistd.Settings.EntityFrameworkCore;

/// <summary>设置 EF Core 持久化的注册与模型配置。</summary>
public static class DependencyInjection
{
    /// <summary>注册 EF Core 设置存储（基于指定 DbContext）。</summary>
    /// <remarks>
    /// 宿主须注册 <c>AddUnitOfWork()</c> 与 <c>AddUnitOfWorkEfCore()</c>，以及 <c>IDistributedLock</c>（内部调用的
    /// <see cref="Leistd.Settings.DependencyInjection.AddSettingsCore"/> 登记的默认写入入口需要它，见该方法）；
    /// 本存储通过 <c>IDbContextProvider&lt;TDbContext&gt;</c> 获取当前工作单元的上下文并参与其事务，
    /// 默认写入入口的独立提交与回滚由此成立。
    /// 同一 DbContext 重复调用幂等；已用另一 DbContext 或其他实现注册过设置存储时抛出 <see cref="InvalidOperationException"/>。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddSettingsEfCore&lt;AppDbContext&gt;();
    ///
    /// // DbContext 里映射设置表
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     base.OnModelCreating(modelBuilder);
    ///     modelBuilder.ConfigureSettings();
    /// }
    /// </code>
    /// </example>
    /// <typeparam name="TDbContext">承载设置表的 DbContext。</typeparam>
    public static IServiceCollection AddSettingsEfCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        // 设置只有一个权威存储：两个上下文各注册一次时会静默取其一
        services.EnsureSingleAuthoritative<ISettingStore, EfCoreSettingStore<TDbContext>>(
            ServiceLifetime.Transient,
            "Settings have a single authoritative store; map SettingRecord in one DbContext.");

        services.AddSettingsCore();
        services.TryAddTransient<ISettingStore, EfCoreSettingStore<TDbContext>>();

        return services;
    }

    /// <summary>将 <see cref="SettingRecord"/> 实体配置应用到 DbContext。在 OnModelCreating 中调用。</summary>
    public static ModelBuilder ConfigureSettings(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SettingRecordConfiguration());
        return modelBuilder;
    }
}
