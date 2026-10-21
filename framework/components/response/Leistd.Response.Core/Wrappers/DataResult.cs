namespace Leistd.Response.Wrappers;

/// <summary>带数据的统一响应结果。</summary>
/// <typeparam name="T">载荷类型。</typeparam>
public record Result<T> : Result
{
    /// <summary>业务载荷。失败时为 <see langword="null"/>。</summary>
    public T? Data { get; init; }

    /// <summary>构造一个带载荷的成功结果（<c>Code = 0</c>）。</summary>
    /// <param name="data">业务载荷。</param>
    /// <param name="message">可选消息。</param>
    public static Result<T> Ok(T data, string? message = null)
        => new() { Code = 0, Data = data, Message = message };

    /// <summary>构造一个业务失败结果，<see cref="Data"/> 保持 <see langword="null"/>。</summary>
    /// <param name="code">非零业务状态码。</param>
    /// <param name="message">失败消息。</param>
    public new static Result<T> Fail(int code, string? message = null)
        => new() { Code = code, Message = message };
}
