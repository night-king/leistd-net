using System.Net;
using Leistd.ExceptionHandling.Descriptors;
using Leistd.ExceptionHandling.Options;
using Leistd.ServiceClient.Exceptions;

namespace Leistd.ServiceClient.ExceptionMappings;

// 服务调用故障在 HTTP 边界的安全默认响应，由 AddServiceClientPipeline 登记；宿主用 MapException<ServiceClientException> 覆盖。
internal static class ServiceClientExceptionMappings
{
    // 上游故障是协议层失败：契约就是 502/503/504 这些状态码本身，只带本地化标题与 traceId，
    // 不合成与状态码一一对应的错误码，也不回显上游的 URL 与响应片段（它们只进服务端日志）。
    private static readonly ExceptionDescriptor InternalError = new((int)HttpStatusCode.InternalServerError);
    private static readonly ExceptionDescriptor BadGateway = new((int)HttpStatusCode.BadGateway);
    private static readonly ExceptionDescriptor Unavailable = new((int)HttpStatusCode.ServiceUnavailable);
    private static readonly ExceptionDescriptor Timeout = new((int)HttpStatusCode.GatewayTimeout);

    // 按本地观测到的失败来源登记。只映射本组件的异常类型，不认领 TaskCanceledException 这类 BCL 异常（否则会接管整个应用的取消）。
    public static void Configure(GlobalExceptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MapDefaultException<ServiceClientException>(exception => exception.FailureKind switch
        {
            ServiceClientFailureKind.InvalidResponse or ServiceClientFailureKind.RemoteFailure => BadGateway,
            ServiceClientFailureKind.Unavailable => Unavailable,
            ServiceClientFailureKind.Timeout => Timeout,
            _ => InternalError
        });
    }
}
