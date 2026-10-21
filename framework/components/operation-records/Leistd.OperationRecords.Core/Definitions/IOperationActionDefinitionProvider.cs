using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.Definitions;

/// <summary>向定义上下文登记本模块的操作动作。</summary>
/// <remarks>
/// <para>登记后的定义提供类别筛选、可见性与严重度；记录器拒绝未登记的动作码。</para>
/// <para>动作码一经发布不要修改：它是历史记录的含义本身。</para>
/// </remarks>
/// <example>
/// <code>
/// public class AppOperationActionDefinitionProvider : IOperationActionDefinitionProvider
/// {
///     public void Define(IOperationActionDefinitionContext context)
///     {
///         context.Add("user.created", "account", OperationVisibility.Tenant);
///         context.Add("permission-grants.replaced", "authorization",
///             OperationVisibility.Tenant, OperationSeverity.Critical);
///     }
/// }
/// </code>
/// </example>
public interface IOperationActionDefinitionProvider
{
    /// <summary>登记操作动作定义。</summary>
    /// <param name="context">定义上下文。</param>
    void Define(IOperationActionDefinitionContext context);
}

/// <summary>操作动作的登记与查询上下文。</summary>
/// <remarks>动作码全局唯一，重复登记在启动期抛出。</remarks>
public interface IOperationActionDefinitionContext
{
    /// <summary>登记一个操作动作。</summary>
    /// <param name="code">动作码，全局唯一且一经发布不可更改（如 <c>user.created</c>）。</param>
    /// <param name="category">
    /// 类别，驱动界面分类筛选；取值由业务定义（如 <c>"account"</c>），框架不预置清单。
    /// </param>
    /// <param name="visibility">
    /// 可见性，必填：按这条记录描述的操作属于谁的边界选择，判错即越权。
    /// </param>
    /// <param name="severity">严重度，默认 <see cref="OperationSeverity.Info"/>。</param>
    /// <param name="targetIsActor">
    /// 目标即操作人：用于主体在动作完成时才被证实的自证类动作（登录成功、注册、改密）。
    /// 只标经过凭据验证的动作；失败登录的用户名这类未经验证的标识不能标。
    /// </param>
    IOperationActionDefinition Add(
        string code,
        string category,
        OperationVisibility visibility,
        OperationSeverity severity = OperationSeverity.Info,
        bool targetIsActor = false);

    /// <summary>按动作码查找定义；不存在时返回 <see langword="null"/>。</summary>
    IOperationActionDefinition? GetOrNull(string code);
}

/// <summary>一个已登记的操作动作定义。</summary>
public interface IOperationActionDefinition
{
    /// <summary>动作码（全局唯一标识）。</summary>
    string Code { get; }

    /// <summary>类别。</summary>
    string Category { get; }

    /// <summary>可见性。</summary>
    OperationVisibility Visibility { get; }

    /// <summary>严重度。</summary>
    OperationSeverity Severity { get; }

    /// <summary>目标即操作人（自证类动作）。</summary>
    bool TargetIsActor { get; }
}

/// <summary>提供操作动作定义的只读索引。</summary>
public interface IOperationActionDefinitionManager
{
    /// <summary>按动作码查找；不存在时返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 返回 <see langword="null"/> 表示未登记的码：写入时记录器直接抛出；读取历史时调用方应降级（如显示裸码），不抛错。
    /// </remarks>
    IOperationActionDefinition? GetOrNull(string code);

    /// <summary>获取全部定义，按登记顺序。</summary>
    IReadOnlyList<IOperationActionDefinition> GetAll();

    /// <summary>获取全部出现过的类别，用于驱动界面的分类筛选项。</summary>
    IReadOnlyList<string> GetCategories();
}
