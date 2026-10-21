namespace Leistd.UnitOfWork.Events;

/// <summary>工作单元事件的执行阶段。</summary>
/// <remarks>
/// <para>只有两个阶段，且都在 <c>CompleteAsync()</c> 内被 <c>await</c>：处理器可以安全地解析
/// 作用域依赖，异常也不会消失在后台任务里。</para>
/// 不提供回滚后或释放后阶段；失败补偿使用 Outbox 或显式 <c>try/catch</c>完成。
/// </remarks>
public enum UnitOfWorkPhase
{
    /// <summary>提交前（SaveChanges 之后、Commit 之前）。</summary>
    /// <remarks>
    /// 事务型工作单元下，处理器抛出会导致事务回滚；非事务型下此前的 SaveChanges 已各自落库，
    /// 处理器抛出只让 <c>CompleteAsync</c> 失败，不撤回已落库的写入。
    /// </remarks>
    BeforeCommit,

    /// <summary>提交后（Commit 成功之后），默认阶段。</summary>
    /// <remarks>
    /// 处理器异常上抛给 <c>CompleteAsync()</c> 的调用方，但事务已提交、不会回滚；
    /// 只适合可重试或可丢弃的副作用，必须与事务一致的外部动作应走 Outbox。
    /// </remarks>
    AfterCommit
}
