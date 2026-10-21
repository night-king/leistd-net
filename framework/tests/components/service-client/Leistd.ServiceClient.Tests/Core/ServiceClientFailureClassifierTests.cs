using System.Net;
using Leistd.ServiceClient.Exceptions;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

/// <summary>服务调用失败按 .NET 官方判别方式分类，不依赖具体弹性库。</summary>
public sealed class ServiceClientFailureClassifierTests
{
    public static TheoryData<Exception, ServiceClientFailureKind> Cases => new()
    {
        // HttpClient.Timeout：取消异常的内层是 TimeoutException
        { new TaskCanceledException("timed out", new TimeoutException()), ServiceClientFailureKind.Timeout },
        // 弹性管道的超时策略：自己的拒绝异常包着被取消的请求（Polly 的 TimeoutRejectedException 即此结构）
        { new ResilienceRejection(new TaskCanceledException()), ServiceClientFailureKind.Timeout },
        { new HttpRequestException(HttpRequestError.ConnectionError, "refused"), ServiceClientFailureKind.Unavailable },
        { new HttpRequestException(HttpRequestError.NameResolutionError, "dns"), ServiceClientFailureKind.Unavailable },
        { new HttpRequestException(HttpRequestError.ResponseEnded, "ended"), ServiceClientFailureKind.InvalidResponse },
        {
            new HttpRequestException("copy failed", new HttpIOException(HttpRequestError.InvalidResponse)),
            ServiceClientFailureKind.InvalidResponse
        },
        // 证书等本地信任问题不是上游不可用
        { new HttpRequestException(HttpRequestError.SecureConnectionError, "tls"), ServiceClientFailureKind.Unknown },
        // 响应状态失败（EnsureSuccessStatusCode）不是传输故障
        { new HttpRequestException("404", null, HttpStatusCode.NotFound), ServiceClientFailureKind.Unknown },
        { new InvalidOperationException("config"), ServiceClientFailureKind.Unknown },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Failures_are_classified_along_the_exception_chain(Exception exception, ServiceClientFailureKind expected)
        => Assert.Equal(expected, ServiceClientFailureClassifier.Classify(exception));

    private sealed class ResilienceRejection(Exception inner) : Exception("rejected", inner);
}
