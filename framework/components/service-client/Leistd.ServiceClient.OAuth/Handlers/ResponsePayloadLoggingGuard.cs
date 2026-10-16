using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Client.SystemNetHttp;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Client.OpenIddictClientEvents;
using static OpenIddict.Client.SystemNetHttp.OpenIddictClientSystemNetHttpHandlers;

namespace Leistd.ServiceClient.OAuth.Handlers;

// OpenIddict 7.x 在事件 6183/6184/6185 处把远端响应原文（令牌响应含 access_token）写进 Error 日志。
// 这里在官方处理器之前接手同一职责：语义与错误码保持官方，取消照常抛出，日志只记状态、类型与长度。
// 上游 openiddict/openiddict-core#2558（8.0）提供官方选项后可移除。
internal static class ResponsePayloadLoggingGuard
{
    // 框架注册的 Client 实际经过的响应：发现文档、JWKS、令牌请求（机器令牌与 Token Exchange）。
    internal static IEnumerable<OpenIddictClientHandlerDescriptor> Descriptors { get; } =
    [
        ExtractJson<ExtractConfigurationResponseContext>.Descriptor,
        ExtractJson<ExtractJsonWebKeySetResponseContext>.Descriptor,
        ExtractJson<ExtractTokenResponseContext>.Descriptor,
        ValidateStatus<ExtractConfigurationResponseContext>.Descriptor,
        ValidateStatus<ExtractJsonWebKeySetResponseContext>.Descriptor,
        ValidateStatus<ExtractTokenResponseContext>.Descriptor
    ];

    // 与官方 OpenIddictHelpers.IsFatal（7.7.2）同一判据，取反：致命异常照常向外抛，不能被转写成一次普通的协议失败。
    // 内层异常与聚合异常逐个检查，包在 HttpRequestException 里的内存耗尽同样算致命。
    internal static bool IsRecoverable(Exception exception) => !IsFatal(exception);

    private static bool IsFatal(Exception exception)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return exception switch
        {
            ThreadAbortException => true,
            OutOfMemoryException and not InsufficientMemoryException => true,
            AggregateException { InnerExceptions: var exceptions } => exceptions.Any(IsFatal),
            { InnerException: { } inner } => IsFatal(inner),
            _ => false
        };
    }

    internal sealed class ExtractJson<TContext> : IOpenIddictClientHandler<TContext> where TContext : BaseExternalContext
    {
        public static OpenIddictClientHandlerDescriptor Descriptor { get; }
            = OpenIddictClientHandlerDescriptor.CreateBuilder<TContext>()
                .AddFilter<OpenIddictClientSystemNetHttpHandlerFilters.RequireHttpUri>()
                .UseSingletonHandler<ExtractJson<TContext>>()
                .SetOrder(ExtractJsonHttpResponse<TContext>.Descriptor.Order - 1)
                .SetType(OpenIddictClientHandlerType.Custom)
                .Build();

        public async ValueTask HandleAsync(TContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Transaction.Response is not null) return;
            // 缺少 HTTP 响应或不是 JSON 时交回官方处理器，由它按原有规则处理或报错。
            if (context.Transaction.GetHttpResponseMessage() is not { } response || !IsJson(response)) return;
            try
            {
                // 解析成功后官方处理器见到已有响应即跳过；取消（调用方令牌已取消）不捕获，照常向外传播。
                context.Transaction.Response = await response.Content.ReadFromJsonAsync(
                    OpenIddictSerializer.Default.Response, context.CancellationToken);
            }
            catch (Exception exception) when (IsRecoverable(exception) &&
                (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested))
            {
                // 异常消息可能带出负载片段，只记类型（JsonException 是正文不合法，其余是读取正文时的 I/O、编码或超时故障）。
                context.Logger.LogError("The remote server returned a response that could not be read as JSON " +
                    "(status {StatusCode}, content type {ContentType}, {Length} bytes, {ExceptionType}).",
                    (int) response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                    response.Content.Headers.ContentLength, exception.GetType().Name);
                context.Reject(error: Errors.ServerError,
                    description: "The remote server returned an invalid JSON response.");
            }
        }

        // 与官方判定一致：application/json，或带 +json 结构化语法后缀的任意类型
        private static bool IsJson(HttpResponseMessage response) =>
            response.Content.Headers.ContentType?.MediaType is { } type &&
            (string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase) ||
             type.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    }

    internal sealed class ValidateStatus<TContext> : IOpenIddictClientHandler<TContext> where TContext : BaseExternalContext
    {
        public static OpenIddictClientHandlerDescriptor Descriptor { get; }
            = OpenIddictClientHandlerDescriptor.CreateBuilder<TContext>()
                .AddFilter<OpenIddictClientSystemNetHttpHandlerFilters.RequireHttpUri>()
                .UseSingletonHandler<ValidateStatus<TContext>>()
                .SetOrder(ValidateHttpResponse<TContext>.Descriptor.Order - 1)
                .SetType(OpenIddictClientHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(TContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Transaction.GetHttpResponseMessage() is not { } response) return ValueTask.CompletedTask;
            // 与官方 ValidateHttpResponse 同一条件与同一错误码，只是不重读响应原文。
            // 成功状态码却没有任何处理器取出响应（不支持的 Content-Type 等）：官方事件 6185 会写原文
            if (response.IsSuccessStatusCode && context.Transaction.Response is null)
            {
                context.Logger.LogError("The remote server returned HTTP {StatusCode} with a response that could not be extracted " +
                    "(content type {ContentType}, {Length} bytes).",
                    (int) response.StatusCode, response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentLength);
                context.Reject(error: Errors.ServerError, description: "The remote server returned an unsupported response.");
                return ValueTask.CompletedTask;
            }
            if (response.IsSuccessStatusCode || !string.IsNullOrEmpty(context.Transaction.Response?.Error)) return ValueTask.CompletedTask;
            context.Logger.LogError("The remote server returned HTTP {StatusCode} without an OAuth error " +
                "(content type {ContentType}, {Length} bytes).",
                (int) response.StatusCode, response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentLength);
            context.Reject(
                error: response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => Errors.InvalidRequest,
                    HttpStatusCode.Unauthorized => Errors.InvalidToken,
                    HttpStatusCode.Forbidden => Errors.InsufficientAccess,
                    HttpStatusCode.TooManyRequests => Errors.SlowDown,
                    HttpStatusCode.ServiceUnavailable => Errors.TemporarilyUnavailable,
                    _ => Errors.ServerError
                },
                description: $"The remote server returned HTTP {(int) response.StatusCode}.");
            return ValueTask.CompletedTask;
        }
    }
}
