namespace Leistd.MultiTenancy.AspNetCore.Options;

/// <summary>租户会话自恢复的选项。</summary>
public sealed class TenantSessionRecoveryOptions
{
    /// <summary>默认的恢复标记响应头。</summary>
    public const string DefaultTenantInvalidHeader = "X-Tenant-Invalid";

    /// <summary>要注销的会话认证方案（通常是 Cookie）；为 <see langword="null"/> 时不注销（纯 Bearer 部署）。</summary>
    public string? SignOutScheme { get; set; }

    /// <summary>恢复响应带的标记头，客户端据此只在租户失效时清除本地租户选择；跨域部署要把它加进 CORS 暴露头。</summary>
    public string TenantInvalidHeader { get; set; } = DefaultTenantInvalidHeader;

    // 缺必填项抛 ArgumentException，ParamName 即属性名
    internal void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(TenantInvalidHeader);
}
