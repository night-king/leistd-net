namespace Leistd.ServiceClient.Exceptions;

/// <summary>把服务调用中捕获的异常归入 <see cref="ServiceClientFailureKind"/>。</summary>
/// <remarks>
/// <para>按 .NET 官方的判别方式沿异常链查找，不依赖具体的弹性库：</para>
/// <list type="bullet">
/// <item><description>传输层失败看 <see cref="HttpRequestError"/>：连接失败、域名解析失败为 <see cref="ServiceClientFailureKind.Unavailable"/>；
/// 响应格式无效、响应提前中断为 <see cref="ServiceClientFailureKind.InvalidResponse"/>。</description></item>
/// <item><description>请求被取消而调用方的令牌没有取消，即为超时：<c>HttpClient.Timeout</c> 抛出的取消异常内层是
/// <see cref="TimeoutException"/>；Microsoft.Extensions.Http.Resilience 的超时策略抛出的拒绝异常内层是被取消的请求。</description></item>
/// </list>
/// <para>调用方自己取消时不应走到这里：该取消应原样抛出，由调用方处理。</para>
/// </remarks>
public static class ServiceClientFailureClassifier
{
    /// <summary>对调用方未主动取消的失败分类。</summary>
    /// <param name="exception">服务调用中捕获的异常。</param>
    /// <returns>失败来源；无法归类时为 <see cref="ServiceClientFailureKind.Unknown"/>。</returns>
    public static ServiceClientFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TimeoutException or OperationCanceledException:
                    return ServiceClientFailureKind.Timeout;
                case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }:
                    return ServiceClientFailureKind.Unavailable;
                case HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded }:
                case HttpIOException { HttpRequestError: HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded }:
                    return ServiceClientFailureKind.InvalidResponse;
            }
        }

        return ServiceClientFailureKind.Unknown;
    }
}
