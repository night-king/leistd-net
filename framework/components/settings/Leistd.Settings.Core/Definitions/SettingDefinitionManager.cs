namespace Leistd.Settings.Definitions;

/// <summary>首次访问时汇总全部 <see cref="ISettingDefinitionProvider"/> 的定义。</summary>
/// <remarks>定义在进程生命周期内不变，一次性构建后只读；名称冲突在构建时抛出。</remarks>
/// <param name="providers">已注册的定义提供者。</param>
public sealed class SettingDefinitionManager(IEnumerable<ISettingDefinitionProvider> providers) : ISettingDefinitionManager
{
    private readonly Lazy<SettingDefinitionContext> _context = new(() =>
    {
        var context = new SettingDefinitionContext();
        var all = providers.ToList();
        foreach (var provider in all)
        {
            provider.Define(context);
        }

        foreach (var provider in all)
        {
            provider.PostDefine(context);
        }

        return context;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc />
    public ISettingDefinition? GetOrNull(string name) => _context.Value.GetOrNull(name);

    /// <inheritdoc />
    public IReadOnlyList<ISettingDefinition> GetAll() => _context.Value.GetAll();
}
