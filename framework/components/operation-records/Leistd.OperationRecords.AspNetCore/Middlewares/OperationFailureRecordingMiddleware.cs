using Leistd.ExceptionHandling;
using Leistd.OperationRecords.AspNetCore.Extensions;
using Leistd.OperationRecords.Models;
using Microsoft.AspNetCore.Http;

namespace Leistd.OperationRecords.AspNetCore.Middlewares;

/// <summary>补记带操作注解的端点业务失败，并原样重抛异常。</summary>
public sealed class OperationFailureRecordingMiddleware(RequestDelegate next)
{
    /// <summary>只记录业务错误码及可公开的消息参数。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BusinessException exception)
        {
            // Exception.Data 与异常文本没有可公开展示的约定。
            await context.RecordFailedOperationAsync(OperationFailure.FromCode(exception.Code, exception.LocalizationData));
            throw;
        }
    }
}
