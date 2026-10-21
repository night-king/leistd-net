namespace Leistd.ServiceClient.Options;

/// <summary>服务客户端配置基类；每个下游服务派生一个具体类型，绑定 <c>Leistd:ServiceClients:&lt;服务名&gt;</c>。</summary>
public class ServiceClientOptions
{
    /// <summary>下游服务基础地址（如 <c>http://order-service</c>），结尾自动补 <c>/</c>。</summary>
    /// <remarks>
    /// 可选且不做启动期校验：宿主可在返回的 <c>IHttpClientBuilder</c> 上自行设置。都未设置时，调用在发出请求时失败。
    /// </remarks>
    public string? BaseAddress { get; set; }

}
