using Microsoft.Extensions.Logging;

namespace Leistd.Authorization.Definitions;

/// <summary>权限定义管理器：加载全部 <see cref="IPermissionDefinitionProvider"/> 并提供定义查询。</summary>
/// <remarks>
/// 定义在构造时一次性加载并预计算，运行期不再遍历定义树；
/// 新增或修改权限定义需重启进程。
/// </remarks>
public class PermissionDefinitionManager : IPermissionDefinitionManager
{
    private readonly PermissionDefinitionContext _context;
    private readonly ILogger<PermissionDefinitionManager> _logger;
    private readonly IReadOnlyList<IPermissionGroupDefinition> _groups;
    private readonly IReadOnlyList<IPermissionDefinition> _permissions;
    private readonly Dictionary<string, string[]> _ancestors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _descendants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _effectivelyEnabled = new(StringComparer.Ordinal);

    /// <summary>一次性加载全部提供方的定义并预计算关系；任一提供方抛异常即构造失败。</summary>
    public PermissionDefinitionManager(
        IEnumerable<IPermissionDefinitionProvider> providers,
        ILogger<PermissionDefinitionManager> logger)
    {
        _context = new PermissionDefinitionContext();
        _logger = logger;

        LoadPermissionDefinitions(providers);

        _groups = _context.GetGroups().ToList();
        _permissions = _context.GetAllPermissions().ToList();
        BuildRelationCaches();
    }

    private void LoadPermissionDefinitions(IEnumerable<IPermissionDefinitionProvider> providers)
    {
        _logger.LogInformation("Loading permission definitions...");

        var providerList = providers.ToList();
        _logger.LogInformation("Found {Count} permission definition provider(s)", providerList.Count);

        foreach (var provider in providerList)
        {
            var providerType = provider.GetType().Name;
            try
            {
                _logger.LogDebug("Loading permission definition provider: {Provider}", providerType);
                provider.Define(_context);
                _logger.LogDebug("Permission definition provider {Provider} loaded", providerType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load permission definition provider {Provider}", providerType);
                throw;
            }
        }

        var allPermissions = _context.GetAllPermissions().ToList();
        _logger.LogInformation("Permission definitions loaded; {Count} permission(s) in total", allPermissions.Count);
    }

    private void BuildRelationCaches()
    {
        foreach (var permission in _permissions)
        {
            var ancestors = new List<string>();
            for (var parent = permission.Parent; parent != null; parent = parent.Parent)
            {
                ancestors.Add(parent.Name);
            }

            _ancestors[permission.Name] = [.. ancestors];

            var descendants = new List<string>();
            CollectDescendants(permission, descendants);
            _descendants[permission.Name] = [.. descendants];

            // 有效启用：自身与全部祖先都启用。
            if (permission.IsEnabled && IsAncestorChainEnabled(permission))
            {
                _effectivelyEnabled.Add(permission.Name);
            }
        }
    }

    private static void CollectDescendants(IPermissionDefinition permission, List<string> target)
    {
        foreach (var child in permission.Children)
        {
            target.Add(child.Name);
            CollectDescendants(child, target);
        }
    }

    private static bool IsAncestorChainEnabled(IPermissionDefinition permission)
    {
        for (var parent = permission.Parent; parent != null; parent = parent.Parent)
        {
            if (!parent.IsEnabled)
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    public IPermissionDefinition? GetOrNull(string name)
    {
        return _context.GetPermissionOrNull(name);
    }

    /// <inheritdoc />
    public IEnumerable<IPermissionDefinition> GetAll()
    {
        return _permissions;
    }

    /// <inheritdoc />
    public IReadOnlyList<IPermissionGroupDefinition> GetGroups()
    {
        return _groups;
    }

    /// <inheritdoc />
    public bool IsEffectivelyEnabled(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && _effectivelyEnabled.Contains(name);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAncestorNames(string name)
    {
        return _ancestors.TryGetValue(name, out var ancestors) ? ancestors : [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDescendantNames(string name)
    {
        return _descendants.TryGetValue(name, out var descendants) ? descendants : [];
    }
}
