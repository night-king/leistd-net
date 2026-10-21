namespace Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;

/// <summary>本地解析（宿主直连控制库）的配置。</summary>
public sealed class LocalTenantConnectionOptions
{
    /// <summary>控制库上下文的连接名（其 <c>[ConnectionStringName]</c> 的值），如 <c>IdentityControl</c>。</summary>
    /// <remarks>
    /// <para>必填，且不能是 <c>Default</c>：控制库固定在宿主连接上、不参与租户路由。
    /// 该名称解析为 <c>ConnectionStrings:{名称}</c>，未配置时回落 <c>ConnectionStrings:Default</c>。</para>
    /// <para>只经委托设置、不绑定配置节：它须与控制库上下文的 <c>[ConnectionStringName]</c> 一致；
    /// 部署换控制库时改 <c>ConnectionStrings:{名称}</c> 的连接串。</para>
    /// </remarks>
    public string? ControlPlaneConnectionStringName { get; set; }
}
