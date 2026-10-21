using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Leistd.Response.AspNetCore.Writers;
using Leistd.Response.Wrappers;

namespace Leistd.Response.AspNetCore.Filters;

/// <summary>将成功响应包装为统一结果；MVC 的错误结果（问题详情）同样换成信封。</summary>
/// <remarks>
/// <c>NotFound()</c>、<c>Problem()</c> 等错误结果由 MVC 的 <c>ProblemDetailsFactory</c> 生成、经输出格式化器写出，
/// 不经过 <c>IProblemDetailsService</c>，信封写入器接不到它们，只能在结果阶段转换。
/// </remarks>
public class ResultWrapperFilter(ILogger<ResultWrapperFilter> logger) : IAsyncResultFilter
{
    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.ActionDescriptor.EndpointMetadata.Any(m => m is Attributes.NoWrapAttribute))
        {
            await next();
            return;
        }

        if (context.Result is ObjectResult { Value: ProblemDetails problem } problemResult)
        {
            var status = problem.Status ?? problemResult.StatusCode ?? StatusCodes.Status500InternalServerError;
            context.Result = new ObjectResult(ProblemDetailsEnvelope.ToResult(problem, status)) { StatusCode = status };
        }
        else if (context.Result is ObjectResult objectResult && objectResult.Value is not Result)
        {
            if (objectResult.StatusCode is null or >= 200 and < 300)
            {
                logger.LogDebug("Wrapping response: {ActionName}", context.ActionDescriptor.DisplayName);

                var wrappedResult = Result<object?>.Ok(objectResult.Value);

                context.Result = new ObjectResult(wrappedResult)
                {
                    StatusCode = objectResult.StatusCode ?? 200
                };
            }
        }

        await next();
    }
}
