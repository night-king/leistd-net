using Leistd.Localization.Json;
using Leistd.Localization.Options;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Leistd.Localization.AspNetCore;

/// <summary>组合本地化工厂：按显式登记的资源标记类型路由，不全局接管宿主既有本地化。</summary>
/// <remarks>
/// 路由精确到类型，不按程序集：<c>TResourceSource</c> 登记在
/// <see cref="JsonLocalizationOptions.JsonResourceTypes"/> 中则走 JSON，其余一律委派官方 RESX 工厂。
/// <see cref="Create(string, string)"/> 一律走官方工厂，不为 baseName 形态做 JSON 猜测。
/// </remarks>
public sealed class CompositeStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly JsonStringLocalizerFactory _json;
    private readonly IStringLocalizerFactory _fallback;
    private readonly HashSet<Type> _jsonResourceTypes;

    /// <summary>创建组合工厂。已登记的资源标记类型走 JSON 实现，其余交回宿主既有工厂。</summary>
    public CompositeStringLocalizerFactory(
        JsonStringLocalizerFactory json,
        ResourceManagerStringLocalizerFactory fallback,
        IOptions<JsonLocalizationOptions> options)
    {
        _json = json;
        _fallback = fallback;
        _jsonResourceTypes = [.. options.Value.JsonResourceTypes];
    }

    /// <summary>仅显式登记为 JSON 资源的类型走 JSON，其余委派官方 RESX 工厂。</summary>
    public IStringLocalizer Create(Type resourceSource)
    {
        ArgumentNullException.ThrowIfNull(resourceSource);
        return _jsonResourceTypes.Contains(resourceSource)
            ? _json.Create(resourceSource)
            : _fallback.Create(resourceSource);
    }

    /// <summary>baseName/location 形态一律委派官方 RESX 工厂。</summary>
    public IStringLocalizer Create(string baseName, string location)
        => _fallback.Create(baseName, location);
}
