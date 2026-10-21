using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace Leistd.AspNetCore.SignalR.Middlewares;

// 浏览器的 WebSocket 与 SSE 不能带自定义头，SignalR 客户端把令牌放在查询串 access_token 里。
// 只在 Hub 端点上转成 Bearer 头并从查询串移除；按端点元数据识别 Hub，不写死路径。
internal sealed class HubAccessTokenMiddleware(RequestDelegate next)
{
    private const string QueryKey = "access_token";

    public Task InvokeAsync(HttpContext context)
    {
        var tokens = context.Request.Query[QueryKey];
        if (tokens.Count == 1
            && !string.IsNullOrWhiteSpace(tokens[0])
            && !context.Request.Headers.ContainsKey("Authorization")
            && context.GetEndpoint()?.Metadata.GetMetadata<HubMetadata>() is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {tokens[0]}";
            context.Request.QueryString = QueryString.Create(
                context.Request.Query
                    .Where(item => !string.Equals(item.Key, QueryKey, StringComparison.Ordinal))
                    .SelectMany(item => item.Value.Select(value => new KeyValuePair<string, string?>(item.Key, value))));
        }

        return next(context);
    }
}
