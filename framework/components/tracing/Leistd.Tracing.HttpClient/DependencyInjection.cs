using Leistd.Tracing.HttpClient.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.Tracing.HttpClient;

/// <summary>关联标识出站透传的注册入口：为 <c>HttpClient</c> 挂上转发处理器。</summary>
public static class DependencyInjection
{
    /// <summary>为 HttpClient 添加关联标识转发处理器：把当前关联标识写入请求头。</summary>
    /// <remarks>按命名客户端登记：同一客户端重复调用只挂一个处理器。</remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddHttpClient("downstream").AddCorrelationIdForwarding();
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddCorrelationIdForwarding(this IHttpClientBuilder builder)
    {
        builder.Services.TryAddTransient<CorrelationIdDelegatingHandler>();

        // 处理器管道按客户端名累加，重复挂载会让每个请求多走一遍处理器。
        if (builder.Services.Any(d => d.ImplementationInstance is ForwardingRegistration registration
                                      && registration.ClientName == builder.Name))
        {
            return builder;
        }

        builder.Services.AddSingleton(new ForwardingRegistration(builder.Name));
        builder.AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
        return builder;
    }

    private sealed record ForwardingRegistration(string ClientName);
}
