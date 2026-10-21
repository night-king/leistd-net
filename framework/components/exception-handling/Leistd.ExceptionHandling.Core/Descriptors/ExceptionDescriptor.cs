using Microsoft.Extensions.Logging;

namespace Leistd.ExceptionHandling.Descriptors;

/// <summary>表示与具体响应序列化形式无关的公开异常描述。</summary>
/// <remarks>
/// 只有业务失败带稳定错误码与公开消息。输入校验、未预期异常、上游故障等协议层失败的契约就是 HTTP 状态码本身
/// （RFC 9457 §4），<paramref name="Code"/> 与 <paramref name="Message"/> 留空：响应只带本地化标题、<c>traceId</c>
/// 与可选的字段错误，不合成与状态码一一对应的错误码。
/// </remarks>
/// <param name="StatusCode">HTTP 状态码。</param>
/// <param name="Code">稳定的业务错误码；协议层失败为 <see langword="null"/>。</param>
/// <param name="Message">允许向调用方公开的消息（Problem Details 的 <c>detail</c>）；协议层失败为 <see langword="null"/>。</param>
/// <param name="Errors">可选的字段级错误。</param>
/// <param name="LogLevel">建议日志级别。</param>
public sealed record ExceptionDescriptor(
    int StatusCode,
    string? Code = null,
    string? Message = null,
    IReadOnlyList<ErrorItem>? Errors = null,
    LogLevel LogLevel = LogLevel.Error);
