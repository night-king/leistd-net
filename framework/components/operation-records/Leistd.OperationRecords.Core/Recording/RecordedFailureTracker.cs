namespace Leistd.OperationRecords.Recording;

/// <summary>记录当前作用域内哪些动作与目标已经留过失败记录，供端点兜底据此跳过重复补记。</summary>
/// <remarks>
/// <para><see cref="IOperationRecorder.RecordFailedAsync"/> 自身不判重，直接调用两次仍会写两条。
/// 判据是动作码加目标；推不出目标时按动作码判。端点注解的目标须与应用服务记录的目标逐字一致。</para>
/// <para>按作用域注册：登记方与读取方须在同一个 DI 作用域。内部集合不是线程安全的，同一作用域内并发留痕须由调用方串行化。</para>
/// <para>自定义 <see cref="IOperationRecorder"/> 实现须在成功写出失败记录之后调用 <see cref="MarkRecorded"/>；写库失败时不要登记。</para>
/// </remarks>
public sealed class RecordedFailureTracker
{
    private readonly Dictionary<string, HashSet<string>> _targetsByAction = new(StringComparer.Ordinal);

    /// <summary>登记一条已经成功写出的失败记录。</summary>
    /// <param name="action">已留痕的动作码。</param>
    /// <param name="targetId">这条记录的目标标识。</param>
    public void MarkRecorded(string action, string targetId)
    {
        if (!_targetsByAction.TryGetValue(action, out var targets))
        {
            targets = new HashSet<string>(StringComparer.Ordinal);
            _targetsByAction[action] = targets;
        }

        targets.Add(targetId);
    }

    /// <summary>判断本作用域内该动作对该目标是否已经留过失败记录。</summary>
    /// <param name="action">要判断的动作码。</param>
    /// <param name="targetId">
    /// 要判断的目标标识；传 <c>null</c> 表示调用方推不出目标，此时该动作留过任何一条记录都算已记。
    /// </param>
    /// <returns>已经留过失败记录时返回 <see langword="true"/>。</returns>
    public bool AlreadyRecorded(string action, string? targetId)
        => _targetsByAction.TryGetValue(action, out var targets)
            && (targetId is null || targets.Contains(targetId));
}
