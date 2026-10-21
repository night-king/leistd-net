namespace Leistd.ExceptionHandling;

/// <summary>可预期且允许向调用方公开的业务失败。</summary>
/// <remarks>
/// <para><see cref="Code"/> 是稳定的机器契约与本地化键；<see cref="Exception.Message"/>
/// 是词条缺失或未启用本地化时可安全展示的默认文案，也可能随异常对象进入日志（如 5xx 的异常链），
/// 不拼入联系方式、密钥等不能进日志的值；这类值经 <see cref="WithData"/> 传给词条占位符。</para>
/// <para>本类不携带 HTTP 状态码，传输边界根据错误码或宿主映射决定响应状态。</para>
/// </remarks>
public class BusinessException : Exception
{
    /// <summary>错误码：稳定的机器契约兼本地化资源键（如 <c>User:EmailAlreadyUsed</c>）。</summary>
    public string Code { get; }

    /// <summary>本地化占位参数，供资源中的具名占位符（如 <c>{Sku}</c>）填充；未启用本地化时忽略。</summary>
    /// <remarks>
    /// 填进错误响应的 <c>detail</c> 返回给调用方，也可能原值进入操作记录与导出；只放这些读者可见的值。
    /// 框架的全局异常处理器不记它，操作记录的日志模式只记参数名。
    /// </remarks>
    public IReadOnlyDictionary<string, object?> LocalizationData => _localizationData;

    private readonly Dictionary<string, object?> _localizationData = new(StringComparer.Ordinal);

    /// <summary>以稳定错误码与安全默认文案构造业务异常。</summary>
    /// <param name="code">错误码兼本地化资源键。</param>
    /// <param name="message">词条缺失时允许向调用方展示的安全默认文案；可能随异常对象进入日志，不拼入联系方式等运行时值。</param>
    /// <param name="innerException">内层异常。</param>
    public BusinessException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
    }

    /// <summary>追加一个本地化占位参数；写入 <see cref="LocalizationData"/>，公开范围同它。</summary>
    /// <param name="name">占位符名，与资源中的 <c>{Name}</c> 对应。</param>
    /// <param name="value">填充值。</param>
    public BusinessException WithData(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _localizationData[name] = value;
        return this;
    }
}
