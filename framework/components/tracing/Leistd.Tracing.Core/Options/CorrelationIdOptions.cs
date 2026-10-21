using Leistd.Tracing.Constants;

namespace Leistd.Tracing.Options;

/// <summary>关联标识的传播配置，配置节 <c>Leistd:CorrelationId</c>。</summary>
public class CorrelationIdOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Leistd:CorrelationId";

    /// <summary>是否经 HTTP 传播关联标识（入站读取、响应回写、出站转发），默认 <see langword="true"/>。</summary>
    /// <remarks>
    /// 全局生效；关闭后进程内的关联标识照常可用（默认取 Activity 的 TraceId），只是不再读写请求头。
    /// 用于面向公网、不采信外部请求头的边缘服务：调用方不能往日志与操作记录里写入自选标识。
    /// 只是不想向某个第三方转发时，不给那个客户端挂转发处理器即可。
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>读取、回写与转发关联标识的请求头名，默认 <c>X-Correlation-Id</c>。</summary>
    public string HeaderName { get; set; } = CorrelationIdConstants.DefaultHeaderName;

    /// <summary>是否把关联标识写入响应头，默认 <see langword="true"/>。</summary>
    public bool SetResponseHeader { get; set; } = true;
}
