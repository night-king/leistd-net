using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.Recording;

/// <summary>在关键操作成功或被拒之后显式调用的审计记录器。</summary>
/// <remarks>
/// <para>显式调用，不由拦截器自动记录：哪些操作值得留痕由业务判断。动作码与授权依据由业务项目定义
/// （如 <c>identity.user.created</c>、<c>AuthenticatedSelf</c>），框架只存不读。</para>
/// <para>动作码与授权依据必填且不得为空白，否则抛 <see cref="ArgumentException"/>；空白目标由
/// <see cref="OperationTarget"/> 收敛为 <see cref="OperationTarget.None"/>。</para>
/// <para>动作码必须已登记（见 <see cref="IOperationActionDefinitionProvider"/>），否则两个方法都抛
/// <see cref="InvalidOperationException"/>：记录的可见性来自动作定义。</para>
/// </remarks>
public interface IOperationRecorder
{
    /// <summary>记录一次成功的操作。</summary>
    /// <remarks>
    /// <para>有环境工作单元时只在提交后可见：数据库适配与业务同一事务持久化，回滚则记录一并回滚，写入失败照常上抛；
    /// 日志适配在提交后输出，输出故障不改变业务结果，提交与输出之间进程退出可能丢记录。</para>
    /// <para><see cref="OperationVisibility.Host"/> 的动作只能在宿主上下文里记成功，否则抛
    /// <see cref="InvalidOperationException"/>：租户上下文的事务连的是租户的库。这类动作应登记为
    /// <see cref="OperationVisibility.Tenant"/>，或切到宿主上下文后再记。</para>
    /// </remarks>
    /// <param name="action">业务动作码，与被拒路径使用的值逐字一致。</param>
    /// <param name="target">操作目标；无目标（如创建类动作）传 <see cref="OperationTarget.None"/>。</param>
    /// <param name="authorizationBasis">授权依据，由业务定义：通常是权限名；不由权限把守的操作传业务自己的标记。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RecordSucceededAsync(
        string action,
        OperationTarget target,
        string authorizationBasis,
        CancellationToken cancellationToken = default);

    /// <summary>记录一次被拒绝或失败的操作。</summary>
    /// <remarks>
    /// <para>写失败只记日志、不抛出，不把原本的 403/400 变成 500；参数与动作码校验失败照常抛出。</para>
    /// <para>写入独立于调用方的事务，调用方回滚后记录仍保留：日志适配立即输出；数据库适配使用第二个事务，
    /// 在只允许单个写事务的数据库（如 SQLite）上，外层已有未提交写入时会等锁直至超时，结果是记一条错误日志、记录丢失。</para>
    /// <para><see cref="OperationVisibility.Host"/> 的失败记录一律写进宿主层，来源租户记在
    /// <see cref="OperationRecordInfo.ActorTenantId"/>。</para>
    /// <para>不接收取消令牌：请求被客户端中断不影响这条记录。</para>
    /// </remarks>
    /// <param name="action">业务动作码，与成功路径使用的值逐字一致。</param>
    /// <param name="target">操作目标；无法解析时传 <see cref="OperationTarget.None"/>。</param>
    /// <param name="authorizationBasis">授权依据：被检查的权限名，或非权限体系的标记。</param>
    /// <param name="failure">失败原因：业务拒绝走错误码；技术异常走 <see cref="OperationFailure.FromDetail"/>，只传可公开的说明。</param>
    Task RecordFailedAsync(
        string action,
        OperationTarget target,
        string authorizationBasis,
        OperationFailure failure = default);
}
