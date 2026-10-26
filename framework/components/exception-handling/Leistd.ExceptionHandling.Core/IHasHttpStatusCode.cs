namespace Leistd.ExceptionHandling;

/// <summary>沿用已知 HTTP 状态的业务异常：全局异常处理器优先用 <see cref="HttpStatusCode"/>，其次才查错误码映射。</summary>
/// <remarks>
/// 只用于转交上游已经判定过的业务拒绝（例如把远端带业务码的 403 原样交还调用方）；业务层自己的失败仍不决定状态，
/// 按错误码映射。值须在 400–599，越界时处理器忽略它，回到错误码映射。
/// </remarks>
public interface IHasHttpStatusCode
{
    /// <summary>响应应使用的 HTTP 状态码。</summary>
    int HttpStatusCode { get; }
}
