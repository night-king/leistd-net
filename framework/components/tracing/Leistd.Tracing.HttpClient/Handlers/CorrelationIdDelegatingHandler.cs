using Leistd.Tracing.Options;
using Microsoft.Extensions.Options;
using Leistd.Tracing.Abstractions;

namespace Leistd.Tracing.HttpClient.Handlers;

/// <summary>出站关联标识处理器：把当前关联标识写入 <c>HttpClient</c> 请求头，向下游透传。</summary>
public class CorrelationIdDelegatingHandler(
    ICorrelationIdProvider correlationIdProvider,
    IOptionsMonitor<CorrelationIdOptions> optionsMonitor) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var correlationId = correlationIdProvider.Get();
        if (!string.IsNullOrEmpty(correlationId) && !request.Headers.Contains(options.HeaderName))
        {
            request.Headers.Add(options.HeaderName, correlationId);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
