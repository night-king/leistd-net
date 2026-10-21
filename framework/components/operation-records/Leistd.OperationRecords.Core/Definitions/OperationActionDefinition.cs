using Leistd.OperationRecords.Models;

namespace Leistd.OperationRecords.Definitions;

internal sealed class OperationActionDefinition(
    string code,
    string category,
    OperationVisibility visibility,
    OperationSeverity severity,
    bool targetIsActor) : IOperationActionDefinition
{
    public string Code { get; } = code;
    public string Category { get; } = category;
    public OperationVisibility Visibility { get; } = visibility;
    public OperationSeverity Severity { get; } = severity;
    public bool TargetIsActor { get; } = targetIsActor;
}

// 动作码到定义的全局注册表：重复即抛（启动期），提供 O(1) 查找。
internal sealed class OperationActionDefinitionRegistry
{
    private readonly Dictionary<string, OperationActionDefinition> _actions = new(StringComparer.Ordinal);
    private readonly List<OperationActionDefinition> _ordered = [];

    public void Register(OperationActionDefinition action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Code, nameof(action));
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Category, nameof(action));

        if (!_actions.TryAdd(action.Code, action))
        {
            throw new InvalidOperationException(
                $"Operation action '{action.Code}' already exists; action codes must be globally unique.");
        }

        _ordered.Add(action);
    }

    public OperationActionDefinition? GetOrNull(string code)
        => _actions.TryGetValue(code, out var action) ? action : null;

    // 保持登记顺序：界面按它渲染筛选项
    public IReadOnlyList<OperationActionDefinition> GetAll() => _ordered;
}

internal sealed class OperationActionDefinitionContext : IOperationActionDefinitionContext
{
    private readonly OperationActionDefinitionRegistry _registry = new();

    public IOperationActionDefinition Add(
        string code,
        string category,
        OperationVisibility visibility,
        OperationSeverity severity = OperationSeverity.Info,
        bool targetIsActor = false)
    {
        var action = new OperationActionDefinition(code, category, visibility, severity, targetIsActor);
        _registry.Register(action);
        return action;
    }

    public IOperationActionDefinition? GetOrNull(string code)
        => string.IsNullOrWhiteSpace(code) ? null : _registry.GetOrNull(code);

    internal IReadOnlyList<OperationActionDefinition> GetAll() => _registry.GetAll();
}
