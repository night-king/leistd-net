namespace Leistd.OperationRecords.Definitions;

// 聚合所有 IOperationActionDefinitionProvider 的登记结果。单例且在构造时一次性求值，重复的动作码在启动期抛出。
internal sealed class OperationActionDefinitionManager : IOperationActionDefinitionManager
{
    private readonly Dictionary<string, OperationActionDefinition> _byCode;
    private readonly IReadOnlyList<IOperationActionDefinition> _all;
    private readonly IReadOnlyList<string> _categories;

    public OperationActionDefinitionManager(IEnumerable<IOperationActionDefinitionProvider> providers)
    {
        var context = new OperationActionDefinitionContext();
        foreach (var provider in providers)
        {
            provider.Define(context);
        }

        var definitions = context.GetAll();
        _byCode = definitions.ToDictionary(definition => definition.Code, StringComparer.Ordinal);
        _all = [.. definitions];

        // 类别按首次出现顺序去重，筛选项顺序跟随登记顺序
        _categories = [.. definitions.Select(definition => definition.Category).Distinct(StringComparer.Ordinal)];
    }

    public IOperationActionDefinition? GetOrNull(string code)
        => !string.IsNullOrWhiteSpace(code) && _byCode.TryGetValue(code, out var definition) ? definition : null;

    public IReadOnlyList<IOperationActionDefinition> GetAll() => _all;

    public IReadOnlyList<string> GetCategories() => _categories;
}
