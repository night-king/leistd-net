using Leistd.ExceptionHandling.Descriptors;

namespace Leistd.ExceptionHandling.Options;

/// <summary>全局异常响应配置。</summary>
public class GlobalExceptionOptions
{
    /// <summary>配置节名 <c>Leistd:GlobalException</c>。</summary>
    public const string SectionName = "Leistd:GlobalException";

    /// <summary>是否在错误响应中包含异常堆栈。默认 <see langword="false"/>。</summary>
    public bool IncludeExceptionDetails { get; set; }

    private readonly Dictionary<string, int> _codeStatusMappings = new(StringComparer.Ordinal);

    /// <summary>当前生效的业务错误码到 HTTP 状态码映射。未命中的业务异常默认映射为 400。</summary>
    /// <remarks>
    /// 只读：写入只经 <see cref="MapCode"/> 与 <see cref="MapDefaultCode"/>，宿主映射优先于组件默认值才与调用顺序无关。
    /// HTTP 状态属于 API 契约，在组合根代码里声明，不从配置文件读取。
    /// </remarks>
    public IReadOnlyDictionary<string, int> CodeStatusMappings => _codeStatusMappings;

    internal Dictionary<Type, Func<Exception, ExceptionDescriptor>> ExceptionMappings { get; } = [];

    /// <summary>将业务错误码映射为指定 HTTP 状态码。</summary>
    public void MapCode(string code, int statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ValidateStatusCode(statusCode);
        _codeStatusMappings[code] = statusCode;
    }

    /// <summary>登记组件拥有的默认状态；宿主的 <see cref="MapCode"/> 始终优先，与调用顺序无关。</summary>
    public void MapDefaultCode(string code, int statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ValidateStatusCode(statusCode);
        _codeStatusMappings.TryAdd(code, statusCode);
    }

    /// <summary>为指定异常类型注册精确的响应描述映射。</summary>
    public void MapException<TException>(Func<TException, ExceptionDescriptor> mapping)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ExceptionMappings[typeof(TException)] = exception => mapping((TException)exception);
    }

    /// <summary>登记组件拥有的默认异常类型映射；宿主对同一类型的 <see cref="MapException{TException}"/> 始终优先。</summary>
    public void MapDefaultException<TException>(Func<TException, ExceptionDescriptor> mapping)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ExceptionMappings.TryAdd(typeof(TException), exception => mapping((TException)exception));
    }

    internal bool TryGetStatusCode(string code, out int statusCode)
        => _codeStatusMappings.TryGetValue(code, out statusCode);

    private static void ValidateStatusCode(int statusCode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 400);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);
    }
}
