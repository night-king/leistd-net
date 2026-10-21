using Leistd.ExceptionHandling;
using Leistd.Response.Wrappers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Leistd.Response.AspNetCore.Writers;

// 问题详情到统一响应信封的唯一映射。ASP.NET Core 有两条写出问题详情的路径：IProblemDetailsService
// （异常、自动模型校验、状态码页、Results.Problem）与 MVC 的错误结果（NotFound()、Problem() 等经输出格式化器写出）。
// 两条路径分别由写入器与结果过滤器接住，但都经这里转换，信封形状只有一处定义。
internal static class ProblemDetailsEnvelope
{
    public static Result ToResult(ProblemDetails problem, int status) => new()
    {
        Code = status,
        Message = problem.Detail ?? problem.Title ?? ReasonPhrases.GetReasonPhrase(status),
        TraceId = problem.Extensions.TryGetValue("traceId", out var traceId) ? traceId?.ToString() : null,
        ErrorCode = problem.Extensions.TryGetValue("code", out var code) ? code as string : null,
        Errors = Errors(problem),
    };

    // 本框架的 errors 扩展是 ErrorItem 列表；框架自带的校验问题（HttpValidationProblemDetails）是字段到消息的字典
    private static IReadOnlyList<ErrorItem>? Errors(ProblemDetails problem)
    {
        if (problem.Extensions.TryGetValue("errors", out var errors) && errors is IReadOnlyList<ErrorItem> items)
            return items;

        return problem is HttpValidationProblemDetails { Errors.Count: > 0 } validation
            ? [.. validation.Errors.SelectMany(pair => pair.Value.Select(message => new ErrorItem(message, pair.Key, null)))]
            : null;
    }
}
