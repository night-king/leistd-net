namespace Leistd.Security.RequestContext;

/// <summary>当前请求的客户端信息；无请求时各项为 null。</summary>
public interface IRequestClientInfo
{
    /// <summary>客户端 IP；HTTP 宿主负责配置受信代理与转发头处理。</summary>
    string? IpAddress { get; }

    /// <summary>User-Agent 原文；缺失或空白时为 null。</summary>
    string? UserAgent { get; }
}
