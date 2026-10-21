using Leistd.ServiceClient.Exceptions;

namespace Leistd.ServiceClient.Handlers;

// 把传输层异常统一为带故障类别的 ServiceClientException，由调用方的异常管道记录并映射状态码。
// 不写日志（IHttpClientFactory 已记录调用摘要）；调用方主动取消与已规范化的异常原样抛出。
internal sealed class TransportFailureHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not ServiceClientException &&
            !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new ServiceClientException(
                $"{request.Method} {request.RequestUri} invocation failed: {ex.Message}", ex,
                ServiceClientFailureClassifier.Classify(ex));
        }
    }
}
