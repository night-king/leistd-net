using Leistd.Settings.Definitions;
using Leistd.Settings.Events;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.Validation;

namespace Leistd.Settings.Management;

/// <summary>设置值的写入入口，写前对照定义校验。</summary>
/// <remarks>业务写入经本接口而不是直接调用 <see cref="ISettingStore"/>：未定义的名称与定义不允许的层级在写入时拒绝。</remarks>
public interface ISettingManager
{
    /// <summary>写入设置值；<paramref name="value"/> 为 <see langword="null"/> 时清除该层级的值。</summary>
    /// <remarks>
    /// <para>清除同样经过业务校验器（以清除后会生效的值），可能因此被拒绝。</para>
    /// <para>默认实现每次调用独立提交：不并入调用方的工作单元或事务，返回即已提交，调用方外层随后回滚也不撤销它；
    /// 同一外层里的两次调用各自提交，后一次失败不撤销前一次。校验或存储失败时本次什么都不落库、不分发事件。</para>
    /// <para>同一租户（宿主算一个）经默认实现的写入串行执行，从候选值校验到提交之间不会插入另一次写入，
    /// 跨设置的 <see cref="ISettingValueValidator"/> 经 <see cref="ISettingProvider"/> 读到的是最新已提交的值。
    /// 这一保证要求相关写入都经本入口、多副本共用同一个跨进程锁、读取器与存储按当前工作单元新鲜读取；
    /// 直接写 <see cref="ISettingStore"/>、外部 SQL 或自带缓存的读取器不在其内。它也不维护"其他用户的生效组合"这类规则：
    /// 校验器只看当前写入者的视角。</para>
    /// <para><see cref="SettingChangedEvent"/> 属于本次提交：提交后才分发，外层回滚不影响它。提交后处理器运行时锁仍被持有，
    /// 不得同步等待同一租户的另一次 <c>SetAsync</c>（会等到自己释放锁，形成死锁）；处理器失败时值已提交，异常原样上抛，
    /// 不代表写入被回滚，也不会重试写入。</para>
    /// </remarks>
    /// <param name="name">设置名称。</param>
    /// <param name="value">设置值；<see langword="null"/> 表示清除，使其回落到下一层。</param>
    /// <param name="scope">写入的层级，必须在定义允许的范围内。</param>
    /// <param name="userId">用户级时的用户标识；租户级传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetAsync(
        string name,
        string? value,
        SettingScopes scope,
        string? userId = null,
        CancellationToken cancellationToken = default);
}
