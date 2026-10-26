using Leistd.ExceptionHandling;

namespace Leistd.ServiceClient.Exceptions;

/// <summary>交还给本服务调用方的上游业务拒绝：沿用上游的错误码、公开说明与 HTTP 状态。</summary>
/// <remarks>由 <see cref="RemoteServiceException.ToBusinessException"/> 创建；内层异常保留远端诊断信息。</remarks>
public sealed class RemoteBusinessException : BusinessException, IHasHttpStatusCode
{
    internal RemoteBusinessException(string code, string message, int httpStatusCode, RemoteServiceException innerException)
        : base(code, message, innerException)
    {
        HttpStatusCode = httpStatusCode;
    }

    /// <summary>上游响应的 HTTP 状态（4xx）。</summary>
    public int HttpStatusCode { get; }
}
