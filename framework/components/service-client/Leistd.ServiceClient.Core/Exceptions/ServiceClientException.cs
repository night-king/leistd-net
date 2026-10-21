namespace Leistd.ServiceClient.Exceptions;

/// <summary>服务调用的本地配置、传输或响应解析失败。</summary>
/// <remarks>
/// 是否重试由调用方根据操作幂等性与失败情况决定。
/// 远端明确返回的错误使用 <see cref="RemoteServiceException"/>。
/// </remarks>
public class ServiceClientException : Exception
{
    /// <summary>失败来源；不代表可以无条件重试。</summary>
    public ServiceClientFailureKind FailureKind { get; }

    /// <summary>创建服务调用异常。</summary>
    /// <param name="message">异常消息，含 HTTP 方法、URL 等请求要素。</param>
    /// <param name="innerException">原始异常。</param>
    /// <param name="failureKind">失败来源。</param>
    public ServiceClientException(string message, Exception? innerException = null,
        ServiceClientFailureKind failureKind = ServiceClientFailureKind.Unknown)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }
}
