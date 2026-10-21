using Leistd.ExceptionHandling;
namespace Leistd.ServiceClient.Exceptions;

/// <summary>远端服务明确返回的失败响应。</summary>
/// <remarks>不自动映射为本地业务异常；宿主可在 API 边界按自身契约处理。</remarks>
public class RemoteServiceException : ServiceClientException
{
    /// <summary>远端响应的 HTTP 状态码；通用异常映射不据此决定本地状态码。</summary>
    public int RemoteStatusCode { get; }

    /// <summary>远端业务错误码（ProblemDetails 的 <c>code</c> 或数字信封的 <c>errorCode</c>）；无法解析时为 <see langword="null"/>。</summary>
    public string? ErrorCode { get; }

    /// <summary>远端 traceId（ProblemDetails 的 <c>traceId</c>）；无法解析时为 <see langword="null"/>。</summary>
    public string? RemoteTraceId { get; }

    /// <summary>远端字段级错误（Problem Details 或响应信封的 <c>errors</c>，数组形或官方字典形）；没有时为空。</summary>
    public IReadOnlyList<ErrorItem> Errors { get; }

    /// <summary>远端原始响应体（截断到 4096 个字符），供无法结构化解析时排查。</summary>
    public string? ResponseBody { get; }

    /// <summary>创建远端服务异常。</summary>
    public RemoteServiceException(
        string message,
        int statusCode,
        string? errorCode = null,
        string? remoteTraceId = null,
        IReadOnlyList<ErrorItem>? errors = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException, ServiceClientFailureKind.RemoteFailure)
    {
        RemoteStatusCode = statusCode;
        ErrorCode = errorCode;
        RemoteTraceId = remoteTraceId;
        Errors = errors ?? [];
        ResponseBody = responseBody;
    }
}
