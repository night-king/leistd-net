using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Handlers;
using Xunit;
using Leistd.TestBase.Doubles;

namespace Leistd.ServiceClient.Tests.Core;

/// <summary>传输异常统一为带故障类别的 <see cref="ServiceClientException"/>；调用方取消原样传出。</summary>
public class TransportFailureHandlerTests
{
    private static HttpMessageInvoker Create(HttpMessageHandler inner) =>
        new(new TransportFailureHandler { InnerHandler = inner });

    [Fact]
    public async Task Transport_failure_is_wrapped_with_its_failure_kind()
    {
        var invoker = Create(new ThrowingHttpMessageHandler(_ =>
            new HttpRequestException(HttpRequestError.ConnectionError, "connection refused")));

        var exception = await Assert.ThrowsAsync<ServiceClientException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/api"), CancellationToken.None));

        Assert.Contains("connection refused", exception.Message);
        Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(ServiceClientFailureKind.Unavailable, exception.FailureKind);
    }

    [Fact]
    public async Task An_already_normalized_failure_passes_through_unchanged()
    {
        var original = new ServiceClientException("configured wrong", failureKind: ServiceClientFailureKind.Configuration);
        var invoker = Create(new ThrowingHttpMessageHandler(_ => original));

        var exception = await Assert.ThrowsAsync<ServiceClientException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/api"), CancellationToken.None));

        Assert.Same(original, exception);
    }

    [Fact]
    public async Task Unexpected_handler_failure_is_not_reported_as_upstream_unavailability()
    {
        var invoker = Create(new ThrowingHttpMessageHandler(_ => new InvalidOperationException("handler defect")));

        var exception = await Assert.ThrowsAsync<ServiceClientException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/api"), CancellationToken.None));

        Assert.Equal(ServiceClientFailureKind.Unknown, exception.FailureKind);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.Unknown)]
    public async Task Non_connection_http_failure_is_not_reported_as_temporary_unavailability(HttpRequestError error)
    {
        var invoker = Create(new ThrowingHttpMessageHandler(_ =>
            new HttpRequestException(error, "request failed")));

        var exception = await Assert.ThrowsAsync<ServiceClientException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/api"), CancellationToken.None));

        Assert.Equal(ServiceClientFailureKind.Unknown, exception.FailureKind);
    }

    [Theory]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.ResponseEnded)]
    public async Task Malformed_or_incomplete_upstream_response_is_classified_as_invalid_response(HttpRequestError error)
    {
        foreach (var transportException in new Exception[]
                 {
                     new HttpRequestException(error, "upstream response failed"),
                     new HttpIOException(error, "upstream response failed")
                 })
        {
            var invoker = Create(new ThrowingHttpMessageHandler(_ => transportException));

            var exception = await Assert.ThrowsAsync<ServiceClientException>(() =>
                invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/api"), CancellationToken.None));

            Assert.Equal(ServiceClientFailureKind.InvalidResponse, exception.FailureKind);
            Assert.Same(transportException, exception.InnerException);
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_unwrapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var invoker = Create(new ThrowingHttpMessageHandler(ct => new TaskCanceledException("canceled", null, ct)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://demo/api"), cts.Token));
    }
}
