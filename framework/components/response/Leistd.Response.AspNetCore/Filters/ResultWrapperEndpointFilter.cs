using Leistd.Response.AspNetCore.Attributes;
using Leistd.Response.Wrappers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;

namespace Leistd.Response.AspNetCore.Filters;

/// <summary>将 Minimal API 端点的成功响应包装为统一结果，与 MVC 的 <see cref="ResultWrapperFilter"/> 同一口径。</summary>
/// <remarks>
/// <para>只包装处理器直接返回的对象与 <c>TypedResults.Ok(value)</c>。</para>
/// <para>其余 <see cref="IResult"/> 原样放行（<c>Created</c>、<c>Accepted</c>、文件与流、重定向、<c>NoContent</c> 与非 2xx），
/// 以保留响应头与内容类型；需要信封时由端点自己放进结果，如 <c>TypedResults.Created(location, Result&lt;T&gt;.Ok(dto))</c>。</para>
/// <para>已是 <see cref="Result"/> 的值与标了 <see cref="NoWrapAttribute"/> 的端点同样原样放行。</para>
/// <para>200 的响应类型元数据由 <c>WithResultWrapper()</c> 同步改写成 <c>Result&lt;T&gt;</c>，文档与实际响应一致；
/// 直接挂本过滤器而不经该扩展方法时，元数据不会被改写。</para>
/// <para>错误不经本过滤器：抛出的异常交给异常处理组件输出 Problem Details，与 MVC 侧一致。</para>
/// </remarks>
public sealed class ResultWrapperEndpointFilter(ILogger<ResultWrapperEndpointFilter> logger) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<NoWrapAttribute>() is not null)
        {
            return result;
        }

        switch (result)
        {
            case Result:
                return result;
            case IResult httpResult:
                if (!TryGetPlainOkValue(httpResult, out var value) || value is Result)
                {
                    return result;
                }

                logger.LogDebug("Wrapping endpoint response: {Endpoint}", context.HttpContext.GetEndpoint()?.DisplayName);
                return TypedResults.Ok(Result<object?>.Ok(value));
            default:
                logger.LogDebug("Wrapping endpoint response: {Endpoint}", context.HttpContext.GetEndpoint()?.DisplayName);
                return Result<object?>.Ok(result);
        }
    }

    // 按具体类型判断而不是按 IValueHttpResult：后者把 Created、Accepted 等带额外语义的结果也算进来，
    // 重建成 JSON 会丢掉它们的响应头
    private static bool TryGetPlainOkValue(IResult result, out object? value)
    {
        // Results<Ok<T>, NotFound, …> 先解一层：实际走到哪一支要看运行时的这个值
        var actual = result is INestedHttpResult nested ? nested.Result : result;
        var type = actual.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Ok<>))
        {
            value = ((IValueHttpResult)actual).Value;
            return true;
        }

        value = null;
        return false;
    }
}
