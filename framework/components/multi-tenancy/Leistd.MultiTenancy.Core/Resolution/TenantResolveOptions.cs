namespace Leistd.MultiTenancy.Resolution;

/// <summary>配置租户解析链。</summary>
public class TenantResolveOptions
{
    /// <summary>按顺序执行的租户解析贡献者；首个给出结论的贡献者终止解析链。</summary>
    public IList<ITenantResolveContributor> Contributors { get; } = [];
}
