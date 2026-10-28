namespace Leistd.MultiTenancy.Exceptions;

/// <summary>租户连接的远端来源暂时不可达；解析器可在约定期限内沿用最后确认的路由。</summary>
/// <remarks>无可用旧路由时解析器重新抛出内层原异常；本异常不决定宿主的 HTTP 状态。</remarks>
public sealed class TenantConnectionUnavailableException : Exception
{
    /// <summary>保留连接来源的原始失败。</summary>
    public TenantConnectionUnavailableException(Exception innerException)
        : base("The tenant connection source is temporarily unavailable.", innerException ?? throw new ArgumentNullException(nameof(innerException))) { }
}
