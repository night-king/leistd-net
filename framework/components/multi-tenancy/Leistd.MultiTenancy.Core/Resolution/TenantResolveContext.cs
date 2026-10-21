namespace Leistd.MultiTenancy.Resolution;

/// <summary>在租户解析贡献者之间传递解析状态。</summary>
/// <param name="serviceProvider">当前请求作用域的服务提供器。</param>
public class TenantResolveContext(IServiceProvider serviceProvider)
{
    /// <summary>当前请求作用域的服务提供器，贡献者从中解析所需服务。</summary>
    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    /// <summary>解析出的租户 Id 或名称。</summary>
    public string? TenantIdOrName { get; set; }

    /// <summary>是否已给出结论；为 <see langword="true"/> 且无租户时即定案为宿主，后续贡献者不再执行。</summary>
    public bool Handled { get; set; }

    /// <summary>判断是否已解析出租户或确定为宿主。</summary>
    public bool HasResolvedTenantOrHost() => Handled || TenantIdOrName is not null;
}
