using System.Diagnostics;
using Leistd.Tracing.Abstractions;
using Leistd.Tracing.Constants;
using Leistd.Tracing.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.Tracing.AspNetCore.Middlewares;

/// <summary>入站关联标识中间件：定案本次请求的关联标识、写入日志作用域，并按配置回写响应头。</summary>
/// <remarks>
/// <para>取值顺序：合法的入站请求头 → 当前 Activity 的 TraceId → 新建；不合法的入站值忽略，不让请求失败。</para>
/// <para>不改写 <see cref="HttpContext.TraceIdentifier"/>：错误响应的 <c>traceId</c> 保持官方的链路标识。</para>
/// <para>应尽量靠近管道前端，使后续中间件的日志都带上关联标识。</para>
/// </remarks>
public class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger,
    IOptionsMonitor<CorrelationIdOptions> optionsMonitor)
{
    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, ICorrelationIdProvider correlationIdProvider)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            await next(context);
            return;
        }

        var correlationId = ReadInbound(context, options.HeaderName)
            ?? Activity.Current?.TraceId.ToHexString()
            ?? ActivityTraceId.CreateRandom().ToHexString();

        if (options.SetResponseHeader)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.TryAdd(options.HeaderName, correlationId);
                return Task.CompletedTask;
            });
        }

        using (correlationIdProvider.Change(correlationId))
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationIdConstants.LogKey] = correlationId }))
        {
            await next(context);
        }
    }

    private string? ReadInbound(HttpContext context, string headerName)
    {
        var value = context.Request.Headers[headerName].ToString();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (CorrelationIdConstants.IsWellFormed(value))
        {
            return value;
        }

        logger.LogDebug(
            "Discarded malformed inbound correlation id from header {HeaderName} (length {Length})",
            headerName, value.Length);
        return null;
    }
}
