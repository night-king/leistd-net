using Leistd.UnitOfWork.Options;

namespace Leistd.UnitOfWork;

/// <summary>创建、复用并追踪环境工作单元。</summary>
public interface IUnitOfWorkManager
{
    /// <summary>当前未完成的工作单元；没有时为 <see langword="null"/>。</summary>
    IUnitOfWork? Current { get; }

    /// <summary>开启或复用一个工作单元边界。</summary>
    /// <param name="options">本次工作单元选项；未传入时使用宿主默认选项。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="options"/> 的 <c>Timeout</c> 不为空且不在 1 秒至 <see cref="int.MaxValue"/> 秒之间，
    /// 或 <c>IsolationLevel</c> 不是已定义的枚举值；判据与启动期校验默认选项相同。
    /// </exception>
    /// <param name="requiresNew">
    /// 是否强制创建独立工作单元。默认并入当前工作单元；不决定事务模式。
    /// </param>
    /// <remarks>
    /// <para>没有当前工作单元时始终创建新边界。并入当前边界时，
    /// <paramref name="options"/> 不会改变已运行工作单元的选项。</para>
    /// <para>由使用它的方法自己开启：当前工作单元存放在 <see cref="AsyncLocal{T}"/> 里，在 <c>async</c> 辅助方法内开启后
    /// 不会带回调用方，调用方随后的写入会各自提交且不报错。本方法因此是同步的。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using var uow = unitOfWorkManager.Begin();
    /// try
    /// {
    ///     await ImportAsync(ct);
    ///     await uow.CompleteAsync(ct);
    /// }
    /// catch
    /// {
    ///     await uow.RollbackAsync();
    ///     throw;
    /// }
    /// </code>
    /// </example>
    IUnitOfWork Begin(UnitOfWorkOptions? options = null, bool requiresNew = false);
}
