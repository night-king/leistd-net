using System.Reflection;
using Leistd.Response.AspNetCore.Attributes;
using Leistd.Response.AspNetCore.Filters;
using Leistd.Response.Wrappers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;

namespace Leistd.Response.AspNetCore.Extensions;

/// <summary>为 Minimal API 端点挂载统一响应包装。</summary>
public static class EndpointConventionBuilderExtensions
{
    /// <summary>为端点或路由组挂载 <see cref="ResultWrapperEndpointFilter"/>，成功响应包装为 <c>Result&lt;object?&gt;</c>。</summary>
    /// <remarks>
    /// <para>MVC 侧的 <c>AddResponseWrapper()</c> 只作用于控制器；组件经 <c>Map*</c> 提供的端点在宿主的路由组上调本方法。
    /// 不要在同一端点链上重复调用：不会重复包装，但每次都多挂一层过滤器。</para>
    /// <para>同时把会被包装的 200 响应的 OpenAPI 元数据改写成 <c>Result&lt;T&gt;</c>。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapGroup("/api/v1")
    ///     .WithResultWrapper()
    ///     .MapGet("/orders/{id}", (long id, OrderService service) =&gt; service.GetAsync(id));
    /// </code>
    /// </example>
    /// <typeparam name="TBuilder">端点约定构建器类型。</typeparam>
    /// <param name="builder">端点或路由组的约定构建器。</param>
    public static TBuilder WithResultWrapper<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter<TBuilder, ResultWrapperEndpointFilter>();
        builder.Finally(RewriteSuccessResponseType);
        return builder;
    }

    // 判据与过滤器同源：只改裸值与 Ok<T>（含 Results<…> 中的 Ok<T> 分支）处理器的 200 元数据，
    // Json(...)、文件这类原样放行的端点不改。在 Finally 里执行，此时推断出的元数据已在端点上。
    private static void RewriteSuccessResponseType(EndpointBuilder endpoint)
    {
        if (endpoint.Metadata.Any(metadata => metadata is NoWrapAttribute))
        {
            return;
        }

        // 处理器的 MethodInfo 由 Minimal API 放进元数据；拿不到（自定义端点源）就不改
        if (endpoint.Metadata.OfType<MethodInfo>().LastOrDefault() is not { } handler)
        {
            return;
        }

        var (wrapsEveryValue, okValueTypes) = Classify(handler.ReturnType);
        if (!wrapsEveryValue && okValueTypes.Count == 0)
        {
            return;
        }

        for (var index = 0; index < endpoint.Metadata.Count; index++)
        {
            if (endpoint.Metadata[index] is not IProducesResponseTypeMetadata produces
                || produces.StatusCode != StatusCodes.Status200OK
                || produces.Type is not { } type
                || type == typeof(void)
                || typeof(Result).IsAssignableFrom(type)
                // 裸值处理器：这一条 200 不管声明成什么，运行时发出的都是信封。
                // IResult 处理器：只有落在 Ok<T> 上的那些分支会被包装，同为 200 的 Json<T> 分支不会
                || (!wrapsEveryValue && !okValueTypes.Contains(type)))
            {
                continue;
            }

            endpoint.Metadata[index] = new ProducesResponseTypeMetadata(
                produces.StatusCode,
                typeof(Result<>).MakeGenericType(type),
                [.. produces.ContentTypes]);
        }
    }

    // 返回类型 → (是不是"发出的每个值都会被包装"的裸值处理器, 会被包装的 Ok<T> 值类型集合)
    private static (bool WrapsEveryValue, HashSet<Type> OkValueTypes) Classify(Type returnType)
    {
        var returned = returnType;
        if (returned.IsGenericType
            && (returned.GetGenericTypeDefinition() == typeof(Task<>)
                || returned.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            returned = returned.GetGenericArguments()[0];
        }

        // 裸值（含 object）：过滤器会包装。object 实际返回异构 IResult 时元数据会被误改，
        // 组件文档因此要求这类端点改用具体类型、Results<…> 或 IResult。
        if (!typeof(IResult).IsAssignableFrom(returned))
        {
            return (returned != typeof(void), []);
        }

        if (!returned.IsGenericType)
        {
            // IResult、NoContent 这类：运行时形状未知或无值，不改
            return (false, []);
        }

        var definition = returned.GetGenericTypeDefinition();
        if (definition == typeof(Ok<>))
        {
            return (false, [returned.GetGenericArguments()[0]]);
        }

        // Results<Ok<A>, Ok<B>, NotFound, …>：收集全部 Ok<T>，其余分支各有自己的状态码或自己的形状
        if (definition.Namespace == typeof(Ok<>).Namespace
            && definition.Name.StartsWith("Results`", StringComparison.Ordinal))
        {
            return (false,
            [
                .. returned.GetGenericArguments()
                    .Where(alternative => alternative.IsGenericType
                        && alternative.GetGenericTypeDefinition() == typeof(Ok<>))
                    .Select(alternative => alternative.GetGenericArguments()[0])
            ]);
        }

        return (false, []);
    }
}
