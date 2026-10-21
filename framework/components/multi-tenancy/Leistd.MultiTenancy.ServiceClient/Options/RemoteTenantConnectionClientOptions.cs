using Leistd.ServiceClient.Options;

namespace Leistd.MultiTenancy.ServiceClient.Options;

/// <summary>远端租户连接存储的客户端配置，配置节 <c>Leistd:ServiceClients:{服务名}</c>。</summary>
public sealed class RemoteTenantConnectionClientOptions : ServiceClientOptions
{
    /// <summary>默认路由前缀，与组件文档里映射 <c>MapTenantConnections</c> 的示例一致。</summary>
    public const string DefaultRoutePrefix = "/api/v1/tenant-connections";

    /// <summary>控制面映射 <c>MapTenantConnections</c> 时用的路由前缀；与控制面不一致时首次解析即失败，不会静默连到别的库。</summary>
    public string RoutePrefix { get; set; } = DefaultRoutePrefix;
}
