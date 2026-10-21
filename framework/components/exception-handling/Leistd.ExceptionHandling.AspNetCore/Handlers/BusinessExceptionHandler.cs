using System.Diagnostics;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Leistd.ExceptionHandling.Descriptors;
using Leistd.ExceptionHandling.Options;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.ExceptionHandling.AspNetCore.Handlers;

/// <summary>将未捕获异常解析为统一描述，转成 Problem Details 后经 <see cref="IProblemDetailsService"/> 写出。</summary>
/// <remarks>
/// 写成 Problem Details 还是统一响应信封，由已注册的 <see cref="IProblemDetailsWriter"/> 决定；
/// 状态码页、<c>Results.Problem()</c> 与框架各中间件的失败响应走的是同一条管道。
/// </remarks>
public sealed class BusinessExceptionHandler(
    IOptionsMonitor<GlobalExceptionOptions> optionsMonitor,
    ILogger<BusinessExceptionHandler> logger,
    IProblemDetailsService problemDetailsService,
    IServiceProvider serviceProvider) : IExceptionHandler
{
    // OpenTelemetry 语义约定的错误类型标签，与官方诊断写入请求耗时指标的同名
    private const string ErrorTypeTag = "error.type";

    private readonly IStringLocalizer? _localizer = serviceProvider.GetService<IStringLocalizer>();
    private readonly JsonNamingPolicy? _jsonNamingPolicy = serviceProvider
        .GetService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()?
        .Value.JsonSerializerOptions.PropertyNamingPolicy;

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        // 框架判定的请求错误（Minimal API 请求体解析失败、请求体过大等）自带状态码。放行后由
        // ExceptionHandlerMiddleware 按 UseGlobalExceptionHandler 登记的 StatusCodeSelector 取该状态码，
        // 经 IProblemDetailsService 写出标准问题详情，与生产环境由状态码页写出的响应同形。
        if (exception is BadHttpRequestException)
            return false;

        var descriptor = Resolve(exception, options);
        Log(httpContext, exception, descriptor);

        var problem = ExceptionProblemDetails.Create(httpContext, descriptor, _localizer);
        if (options.IncludeExceptionDetails && !string.IsNullOrEmpty(exception.StackTrace))
            problem.Extensions["stackTrace"] = exception.StackTrace;

        httpContext.Response.StatusCode = descriptor.StatusCode;
        var handled = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
        if (handled)
            TagServerError(httpContext, exception);
        return handled;
    }

    // 已处理的异常不再经官方诊断给请求耗时指标补 error.type（只有不抑制诊断时才补），5xx 由这里补回；
    // 4xx 是预期失败，不标。宿主已写的同名标签不覆盖。
    private static void TagServerError(HttpContext httpContext, Exception exception)
    {
        if (httpContext.Response.StatusCode < StatusCodes.Status500InternalServerError
            || httpContext.Features.Get<IHttpMetricsTagsFeature>() is not { } metricsTags
            || metricsTags.Tags.Any(tag => tag.Key == ErrorTypeTag))
            return;
        metricsTags.Tags.Add(new KeyValuePair<string, object?>(ErrorTypeTag, exception.GetType().FullName));
    }

    private ExceptionDescriptor Resolve(Exception exception, GlobalExceptionOptions options)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (options.ExceptionMappings.TryGetValue(type, out var mapping))
            {
                var mapped = mapping(exception);
                return mapped is { Code: { } code, Message: { } message }
                    ? mapped with { Message = Localize(code, message) }
                    : mapped;
            }
        }

        if (exception is BusinessException businessException)
        {
            var statusCode = options.TryGetStatusCode(businessException.Code, out var mapped)
                ? mapped
                : StatusCodes.Status400BadRequest;
            return new ExceptionDescriptor(
                statusCode,
                businessException.Code,
                Localize(businessException.Code, businessException.Message, businessException.LocalizationData),
                LogLevel: statusCode >= StatusCodes.Status500InternalServerError
                    ? LogLevel.Error
                    : LogLevel.Warning);
        }

        // 输入校验与未预期异常都是协议层失败：契约就是状态码本身（RFC 9457 §4），
        // 只带本地化标题与字段错误，不合成业务码。
        if (exception is ValidationException validationException)
        {
            var field = validationException.ValidationResult?.MemberNames.FirstOrDefault() ?? string.Empty;
            if (_jsonNamingPolicy is not null && field.Length > 0)
                field = _jsonNamingPolicy.ConvertName(field);
            return new ExceptionDescriptor(
                StatusCodes.Status400BadRequest,
                Errors: [new ErrorItem(validationException.Message, field, null)],
                LogLevel: LogLevel.Information);
        }

        return new ExceptionDescriptor(StatusCodes.Status500InternalServerError);
    }

    // 按错误码查公开文案并填充具名占位符；未启用本地化、词条缺失或查询出错时回落到 fallback。
    // 本地化失败不得覆盖原本要返回的错误，因此查询全程吞异常。
    private string Localize(
        string code,
        string fallback,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        if (_localizer is null)
            return fallback;

        try
        {
            var localized = _localizer[code];
            return localized.ResourceNotFound ? fallback : LocalizationPlaceholders.Fill(localized.Value, data);
        }
        catch
        {
            return fallback;
        }
    }

    private void Log(HttpContext httpContext, Exception exception, ExceptionDescriptor descriptor)
    {
        // 与问题详情里官方写出的 traceId 同值（W3C 格式，第二段是链路 TraceId），按响应里的值就能搜到这条日志
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        if (descriptor.LogLevel >= LogLevel.Error)
        {
            logger.Log(
                descriptor.LogLevel,
                exception,
                "Unhandled exception: TraceId={TraceId}, StatusCode={StatusCode}, Code={Code}, " +
                "ExceptionType={ExceptionType}, Path={Path}",
                traceId,
                descriptor.StatusCode,
                descriptor.Code,
                exception.GetType().Name,
                httpContext.Request.Path);
            return;
        }

        // 预期失败只记诊断字段：返回给调用方的 detail 面向提交者，可能回显其输入（邮箱等），不进日志。
        // TraceId 与响应同值；有操作记录时按日志作用域里的关联标识对上审计内容。也不附带异常对象，免得异常链把文本带进来
        logger.Log(
            descriptor.LogLevel,
            "Expected exception: TraceId={TraceId}, StatusCode={StatusCode}, Code={Code}, " +
            "ExceptionType={ExceptionType}, Path={Path}",
            traceId,
            descriptor.StatusCode,
            descriptor.Code,
            exception.GetType().Name,
            httpContext.Request.Path);
    }
}
