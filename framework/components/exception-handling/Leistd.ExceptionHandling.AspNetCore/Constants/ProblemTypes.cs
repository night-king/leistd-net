namespace Leistd.ExceptionHandling.AspNetCore.Constants;

/// <summary>ProblemDetails 的问题类型（<c>type</c>）标识。</summary>
/// <remarks>
/// 只为超出"HTTP 状态码语义"的问题定义类型：业务错误带稳定的 <c>code</c> 扩展，校验错误带 <c>errors</c> 扩展。
/// 其余失败（未预期异常、上游故障、路由不存在等）只有状态码语义，不设本框架的类型，由 ASP.NET Core 按状态码
/// 补默认值（RFC 9457 §4、§4.2.1）。采用 <c>urn:</c> 非解析 URI（§3.1.1 允许），不假定任何托管文档域名。
/// </remarks>
public static class ProblemTypes
{
    /// <summary>校验错误的问题类型；自动模型校验与显式验证异常共用 <c>errors</c> 扩展契约。</summary>
    public const string ValidationError = "urn:leistd:problem:validation-error";

    /// <summary>业务错误的问题类型，带 <c>code</c> 扩展。</summary>
    public const string BusinessError = "urn:leistd:problem:business-error";
}
