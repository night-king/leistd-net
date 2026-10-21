using Leistd.Localization;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Errors;
using Leistd.OperationRecords.Queries;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.OperationRecords;

/// <summary>操作记录核心服务注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册操作记录的记录器与动作定义，并登记 <see cref="OperationFailureCodes"/> 的默认中英译文。</summary>
    /// <remarks>
    /// <para>还需要一个 <see cref="IOperationRecordWriter"/> 实现（数据库存储
    /// <c>AddOperationRecordsEfCore&lt;TDbContext&gt;()</c>，或结构化日志输出 <c>AddOperationRecordsLogging()</c>），
    /// 缺失时解析 <see cref="IOperationRecorder"/> 失败。</para>
    /// <para>不注册历史查询：查询只在有可回读存储时成立，由存储适配调用 <see cref="AddOperationRecordQueries"/>。</para>
    /// <para>选项不绑定配置节，只经 <paramref name="configure"/> 修改。</para>
    /// <para>可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddOperationRecordsEfCore&lt;AppDbContext&gt;();   // 内部已调用 AddOperationRecords()
    ///
    /// // 宿主签发的 claim 用了别的名字时
    /// builder.Services.AddOperationRecords(options =&gt; options.ImpersonatorIdClaimType = "act_sub");
    ///
    /// // 用例里显式记录
    /// await operationRecorder.RecordSucceededAsync(
    ///     OperationRecordActions.UserCreated,
    ///     OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
    ///     "App.Users.Create",
    ///     ct);
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">选项配置委托，在默认值之后应用。</param>
    public static IServiceCollection AddOperationRecords(
        this IServiceCollection services,
        Action<OperationRecordOptions>? configure = null)
    {
        var options = services.AddOptions<OperationRecordOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // 本组件自产的失败码随包带中英译文；宿主资源里的同名键覆盖它。
        services.AddJsonLocalizationResources(typeof(OperationFailureCodes).Assembly);

        // 幂等：EF 包的注册入口也会调到这里。
        // 失败记录去重的状态必须是 Scoped：记录器是 Transient，状态放在它身上会随每次解析重置。
        services.TryAddScoped<RecordedFailureTracker>();
        services.TryAddTransient<IOperationRecorder, OperationRecorder>();

        // 动作定义索引是启动期事实，单例；宿主用 AddSingleton<IOperationActionDefinitionProvider, XxxProvider>() 登记动作。
        services.TryAddSingleton<IOperationActionDefinitionManager, OperationActionDefinitionManager>();
        return services;
    }

    /// <summary>注册历史查询与导出用例（<see cref="IOperationRecordQueryService"/>）。</summary>
    /// <remarks>
    /// 由能回读历史的存储适配调用（如 <c>AddOperationRecordsEfCore&lt;TDbContext&gt;()</c>），宿主通常不直接调用。
    /// 要求已注册 <see cref="IOperationRecordReader"/>。
    /// 可重复调用：服务只注册一次。
    /// </remarks>
    public static IServiceCollection AddOperationRecordQueries(this IServiceCollection services)
    {
        services.AddOperationRecords();
        services.TryAddTransient<IOperationRecordQueryService, OperationRecordQueryService>();
        return services;
    }
}
