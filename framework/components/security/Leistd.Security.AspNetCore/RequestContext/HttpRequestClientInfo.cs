using Leistd.Security.RequestContext;
using Microsoft.AspNetCore.Http;

namespace Leistd.Security.AspNetCore.RequestContext;

/// <summary>从调用时的 HTTP 上下文读取客户端信息。</summary>
public sealed class HttpRequestClientInfo(IHttpContextAccessor contexts) : IRequestClientInfo
{
    /// <inheritdoc />
    public string? IpAddress => contexts.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <inheritdoc />
    public string? UserAgent
    {
        get
        {
            var value = contexts.HttpContext?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
