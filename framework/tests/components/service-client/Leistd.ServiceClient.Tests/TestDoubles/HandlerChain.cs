using Microsoft.Extensions.DependencyInjection;

namespace Leistd.ServiceClient.Tests.TestDoubles;

/// <summary>展开命名客户端实际构建出的处理器链，按类型计数。</summary>
/// <remarks>
/// 处理器经 <c>IConfigureOptions&lt;HttpClientFactoryOptions&gt;</c> 累加，<c>AssertIdempotent</c> 不计这一族，
/// 重复挂载只能从构建出的链上看出来。
/// </remarks>
internal static class HandlerChain
{
    public static int Count<THandler>(IServiceProvider provider, string clientName)
        where THandler : DelegatingHandler
    {
        var count = 0;
        HttpMessageHandler? current = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (current is DelegatingHandler delegating)
        {
            if (delegating is THandler)
            {
                count++;
            }

            current = delegating.InnerHandler;
        }

        return count;
    }
}
