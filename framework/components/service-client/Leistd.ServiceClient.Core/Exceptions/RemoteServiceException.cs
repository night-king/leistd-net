using Leistd.ExceptionHandling;
namespace Leistd.ServiceClient.Exceptions;

/// <summary>远端服务明确返回的失败响应。</summary>
/// <remarks>不自动映射为本地业务异常；宿主可在 API 边界按自身契约处理。</remarks>
public class RemoteServiceException : ServiceClientException
{
    /// <summary>远端响应的 HTTP 状态码；通用异常映射不据此决定本地状态码。</summary>
    public int RemoteStatusCode { get; }

    /// <summary>远端业务错误码或令牌端点的 OAuth 错误码（ProblemDetails 的 <c>code</c> 或数字信封的 <c>errorCode</c>）；无法解析时为 <see langword="null"/>。</summary>
    public string? ErrorCode { get; }

    /// <summary>错误码来自令牌端点实际返回的 OAuth 错误，而不是业务接口或本地解析。</summary>
    public bool IsOAuthError { get; init; }

    /// <summary>远端 traceId（ProblemDetails 的 <c>traceId</c>）；无法解析时为 <see langword="null"/>。</summary>
    public string? RemoteTraceId { get; }

    /// <summary>远端字段级错误（Problem Details 或响应信封的 <c>errors</c>，数组形或官方字典形）；没有时为空。</summary>
    public IReadOnlyList<ErrorItem> Errors { get; }

    /// <summary>远端原始响应体（截断到 4096 个字符），供无法结构化解析时排查。</summary>
    public string? ResponseBody { get; }

    /// <summary>远端业务错误的公开说明（ProblemDetails 的 <c>detail</c> 或信封的 <c>message</c>）；只在带业务码时还原，否则为 <see langword="null"/>。</summary>
    /// <remarks>远端已按调用时的语言本地化，可作为转交给调用方的说明；异常 <see cref="Exception.Message"/> 含地址与远端 traceId，只供诊断。</remarks>
    public string? Detail { get; }

    /// <summary>创建远端服务异常。</summary>
    public RemoteServiceException(
        string message,
        int statusCode,
        string? errorCode = null,
        string? remoteTraceId = null,
        IReadOnlyList<ErrorItem>? errors = null,
        string? responseBody = null,
        Exception? innerException = null,
        string? detail = null)
        : base(message, innerException, ServiceClientFailureKind.RemoteFailure)
    {
        Detail = detail;
        RemoteStatusCode = statusCode;
        ErrorCode = errorCode;
        RemoteTraceId = remoteTraceId;
        Errors = errors ?? [];
        ResponseBody = responseBody;
    }

    /// <summary>远端是带业务码的 4xx 拒绝时，转成沿用远端状态、错误码与公开说明的本地业务异常；否则返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 只在调用方明确要把上游的业务拒绝交还自己的调用方时使用（如代表用户转交写入）；其余远端失败仍按依赖故障处理。
    /// 说明取 <see cref="Detail"/>，没有时用 <paramref name="fallbackMessage"/>；本异常作为内层，诊断信息只进日志。
    /// 本地有同码词条时，全局异常处理器照常按本地词条本地化。
    /// </remarks>
    /// <param name="fallbackMessage">远端没给说明时的安全默认文案。</param>
    public BusinessException? ToBusinessException(string fallbackMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackMessage);
        return ErrorCode is { Length: > 0 } code && RemoteStatusCode is >= 400 and <= 499
            ? new RemoteBusinessException(code, string.IsNullOrWhiteSpace(Detail) ? fallbackMessage : Detail, RemoteStatusCode, this)
            : null;
    }
}
