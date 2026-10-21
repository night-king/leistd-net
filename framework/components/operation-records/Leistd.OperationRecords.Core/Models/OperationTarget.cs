namespace Leistd.OperationRecords.Models;

/// <summary>操作的目标：标识与名字快照捆绑传递，避免与授权依据等字符串参数互换。</summary>
/// <remarks>名字是快照，不是外键：改名或销号后仍保留当时的名字。</remarks>
public readonly record struct OperationTarget
{
    /// <summary>没有目标时的占位标识。</summary>
    public const string NoTargetId = "-";

    private OperationTarget(string id, string? name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>目标标识；没有目标时为 <see cref="NoTargetId"/>。</summary>
    public string Id { get; }

    /// <summary>目标名快照；取不到时为 <see langword="null"/>（授权阶段的拒绝不回填名字）。</summary>
    public string? Name { get; }

    /// <summary>没有目标的操作（如创建类动作在授权阶段被拒时）。</summary>
    public static OperationTarget None { get; } = new(NoTargetId, null);

    /// <summary>由标识与可选的名字快照构造。</summary>
    /// <param name="id">目标标识；为空白时退化为 <see cref="None"/>。</param>
    /// <param name="name">名字快照；空白视为 <see langword="null"/>。同类记录须取同一种名字（登录名或显示名）。</param>
    public static OperationTarget For(string? id, string? name = null)
        => string.IsNullOrWhiteSpace(id) ? None : new(id.Trim(), Normalize(name));

    /// <summary>由 <see cref="Guid"/> 标识与可选的名字快照构造。</summary>
    public static OperationTarget For(Guid id, string? name = null)
        => new(id.ToString(), Normalize(name));

    // 空白名字收敛成 null，不落空串
    private static string? Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
