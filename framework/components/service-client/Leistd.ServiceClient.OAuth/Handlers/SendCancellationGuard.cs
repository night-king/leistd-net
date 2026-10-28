using Microsoft.Extensions.Logging;
using OpenIddict.Client;
using OpenIddict.Client.SystemNetHttp;
using static OpenIddict.Client.OpenIddictClientEvents;
using static OpenIddict.Client.SystemNetHttp.OpenIddictClientSystemNetHttpHandlers;

namespace Leistd.ServiceClient.OAuth.Handlers;

// OpenIddict 7.x 发送请求时宽泛捕获异常：调用方主动取消也记事件 6182 Error 并拒绝为 server_error，
// 入站请求被放弃（导航、关闭页面）就留下网络故障式的错误日志，取消也被当成远端故障向上报告。
// 这里替换官方发送处理器：调用方令牌已取消时照常抛出取消；
// 超时、连接失败仍记事件 6182 Error，原始异常向外传播，由令牌缓存按传输来源分类。
// 升级 OpenIddict 时同时复核取消传播、事件 6182 与真实失败来源的契约。
internal static class SendCancellationGuard
{
    // 框架注册的 Client 实际经过的请求：发现文档、JWKS、令牌请求（机器令牌与 Token Exchange）。
    internal static IEnumerable<OpenIddictClientHandlerDescriptor> Replaced { get; } =
    [
        SendHttpRequest<ApplyConfigurationRequestContext>.Descriptor,
        SendHttpRequest<ApplyJsonWebKeySetRequestContext>.Descriptor,
        SendHttpRequest<ApplyTokenRequestContext>.Descriptor
    ];

    internal static IEnumerable<OpenIddictClientHandlerDescriptor> Descriptors { get; } =
    [
        Send<ApplyConfigurationRequestContext>.Descriptor,
        Send<ApplyJsonWebKeySetRequestContext>.Descriptor,
        Send<ApplyTokenRequestContext>.Descriptor
    ];

    internal sealed class Send<TContext> : IOpenIddictClientHandler<TContext> where TContext : BaseExternalContext
    {
        public static OpenIddictClientHandlerDescriptor Descriptor { get; }
            = OpenIddictClientHandlerDescriptor.CreateBuilder<TContext>()
                .AddFilter<OpenIddictClientSystemNetHttpHandlerFilters.RequireHttpUri>()
                .UseSingletonHandler<Send<TContext>>()
                .SetOrder(SendHttpRequest<TContext>.Descriptor.Order)
                .SetType(OpenIddictClientHandlerType.Custom)
                .Build();

        public async ValueTask HandleAsync(TContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var request = context.Transaction.GetHttpRequestMessage() ??
                throw new InvalidOperationException("The System.Net.Http request cannot be resolved.");
            // 与官方一致：客户端由本处理器释放
            using var client = context.Transaction.GetHttpClient() ??
                throw new InvalidOperationException("The System.Net.Http client cannot be resolved.");

            HttpResponseMessage response;
            try
            {
                // 与官方一致缓冲整个响应，后续处理器可多次读取
                response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (ResponsePayloadLoggingGuard.IsRecoverable(exception))
            {
                // 事件号与日志文本保持官方原值（ID6182），按 6182 排查网络故障的诊断不受影响；
                // 传输异常只含地址与底层原因，不含响应正文
                context.Logger.LogError(6182, exception, "A network error occured while communicating with the remote HTTP server.");
                throw;
            }

            context.Transaction.SetProperty(typeof(HttpResponseMessage).FullName!, response);
        }
    }
}
