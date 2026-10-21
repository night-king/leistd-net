using Leistd.ExceptionHandling.AspNetCore.Constants;
using Leistd.ExceptionHandling.Descriptors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Leistd.ExceptionHandling.AspNetCore.Handlers;

// 把协议中立的描述转成 Problem Details，再统一交给 IProblemDetailsService 写出。
// 异常处理器与自动模型校验共用这一处，两条路径的类型、字段与标题取法一致；
// 最终序列化成 Problem Details 还是统一响应信封，由已注册的 IProblemDetailsWriter 决定。
// traceId 不在这里写：它由 ProblemDetailsOptions.CustomizeProblemDetails 为所有问题详情统一补上。
internal static class ExceptionProblemDetails
{
    public static ProblemDetails Create(
        HttpContext httpContext,
        ExceptionDescriptor descriptor,
        IStringLocalizer? localizer,
        string? titleFallback = null)
    {
        var problem = new ProblemDetails
        {
            // 校验问题与业务问题是本框架定义的问题类型（各自带 errors / code 扩展）；其余只有状态码语义，
            // 不设 type，由 ASP.NET Core 按状态码补默认值，与状态码页、框架各中间件写出的问题详情一致
            Type = descriptor.Errors is { Count: > 0 }
                ? ProblemTypes.ValidationError
                : descriptor.Code is not null ? ProblemTypes.BusinessError : null,
            Title = ProblemTitles.Localize(localizer, descriptor.StatusCode, titleFallback),
            Status = descriptor.StatusCode,
            Detail = descriptor.Message,
            Instance = httpContext.Request.Path
        };
        if (descriptor.Code is not null)
            problem.Extensions["code"] = descriptor.Code;
        if (descriptor.Errors is { Count: > 0 })
            problem.Extensions["errors"] = descriptor.Errors;
        return problem;
    }
}
