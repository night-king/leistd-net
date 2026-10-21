namespace Leistd.MultiTenancy.Resolution;

/// <summary>从单一来源尝试确定当前租户。</summary>
public interface ITenantResolveContributor
{
    /// <summary>贡献者名称，用于诊断日志。</summary>
    string Name { get; }

    /// <summary>尝试解析租户；给出结论时设置 <see cref="TenantResolveContext.TenantIdOrName"/> 或 <see cref="TenantResolveContext.Handled"/>。</summary>
    Task ResolveAsync(TenantResolveContext context);
}
