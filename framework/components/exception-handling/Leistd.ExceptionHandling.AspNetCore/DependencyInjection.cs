using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Leistd.ExceptionHandling.AspNetCore.Handlers;
using Leistd.ExceptionHandling.AspNetCore.Validation;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Leistd.ExceptionHandling.Options;
using Leistd.ExceptionHandling.Descriptors;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.ExceptionHandling.AspNetCore;

/// <summary>全局异常处理的注册与管道接入入口。</summary>
public static class DependencyInjection
{
    /// <summary>将自动模型校验响应接入统一失败管道：经 <see cref="IProblemDetailsService"/> 写出，默认为 Problem Details。</summary>
    /// <remarks>
    /// <para>在 <c>AddControllers()</c> 后调用，使 400 与业务校验的 <c>errors</c> 结构一致。</para>
    /// <para><c>errors[].field</c> 跟随宿主的 JSON 命名策略（请求体里叫 <c>name</c>，这里就是 <c>name</c>），
    /// 请求体、查询参数与嵌套对象（<c>address.city</c>）一致，调用方据此把错误落回对应的输入项；
    /// 默认 Problem Details 的标题按 <c>Title:{状态码}</c> 本地化。</para>
    /// <para>字段名换算只作用于属性式 DTO（<c>{ get; init; }</c>）。位置记录（<c>record X([Required] string Name)</c>）
    /// 的校验键取自构造参数，ASP.NET 不对它应用命名策略，仍是 C# 参数名——输入 DTO 应写成属性式。</para>
    /// <para><see cref="System.ComponentModel.DataAnnotations.IValidatableObject"/> 产出的错误与特性错误同一口径：
    /// <c>MemberNames</c> 写 C# 属性名（<c>nameof(Roles)</c>），字段名同样换成 JSON 名；对不上任何属性的成员名原样使用，
    /// 不给成员名的错误落在模型自身的键上。
    /// 宿主启用了 DataAnnotations 本地化（<c>AddDataAnnotationsLocalization</c>）时，<c>ErrorMessage</c> 作资源键、
    /// 经 <c>DataAnnotationLocalizerProvider</c> 按该 DTO 类型取本地化器翻译，因此应写不含运行时值的固定英文句；
    /// 未启用时原文返回。</para>
    /// </remarks>
    public static IMvcBuilder ConfigureApiValidation(this IMvcBuilder builder)
    {
        if (builder.Services.Any(service => service.ServiceType == typeof(ApiValidationRegistrationMarker)))
            return builder;
        builder.Services.AddSingleton<ApiValidationRegistrationMarker>();
        builder.Services.AddProblemDetails();
        RegisterProblemDetailsConventions(builder.Services);

        // 模型校验键默认是 C# 属性名，改用 JSON 命名策略，与请求体和显式验证的字段名一致；宿主没设命名策略时无需处理。
        // 绑定时已按属性名建好的条目与 IValidatableObject 的成员名由写出时的换算补齐。
        builder.Services.AddOptions<MvcOptions>()
            .Configure<IOptions<JsonOptions>>((mvcOptions, jsonOptions) =>
            {
                if (jsonOptions.Value.JsonSerializerOptions.PropertyNamingPolicy is { } namingPolicy)
                {
                    mvcOptions.ModelMetadataDetailsProviders.Add(
                        new SystemTextJsonValidationMetadataProvider(namingPolicy));
                }
            });

        // IValidatableObject.Validate 的文案默认不本地化，与特性文案两种口径。替换 DataAnnotations 提供器为它追加的
        // 那一项，须排在该提供器之后：MVC 在 Configure 阶段登记它，这里用 PostConfigure。
        builder.Services.AddOptions<MvcOptions>()
            .PostConfigure<IOptions<MvcDataAnnotationsLocalizationOptions>, IServiceProvider>(
                (mvcOptions, localizationOptions, serviceProvider) => mvcOptions.ModelValidatorProviders.Add(
                    new ValidatableObjectModelValidatorProvider(
                        localizationOptions, serviceProvider.GetService<IStringLocalizerFactory>())));

        // 请求体读不成 JSON 时不回显 System.Text.Json 的解析细节，与 Minimal API 路径一致
        builder.Services.Configure<JsonOptions>(options => options.AllowInputFormatterExceptionMessages = false);

        builder.Services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var metadataProvider = context.HttpContext.RequestServices.GetRequiredService<IModelMetadataProvider>();
                var errors = context.ModelState
                    .Where(entry => entry.Value is { Errors.Count: > 0 })
                    .SelectMany(entry => entry.Value!.Errors.Select(error => new ErrorItem(
                        // 只有异常、没有文案的错误（如读不成 JSON）与 MVC 自带的校验问题同样回落到通用句
                        Detail: string.IsNullOrEmpty(error.ErrorMessage) ? "The input was not valid." : error.ErrorMessage,
                        // 查询参数等逐属性绑定的来源，模型状态的键是绑定时的属性名，换回校验模型名
                        Field: ValidationFieldNames.Resolve(context, metadataProvider, entry.Key),
                        Code: null)))
                    .ToArray();
                var localizer = context.HttpContext.RequestServices.GetService<IStringLocalizer>();
                // 输入校验是协议层失败：只带字段错误与本地化标题，不合成业务码
                var descriptor = new ExceptionDescriptor(StatusCodes.Status400BadRequest, Errors: errors);
                // 未启用本地化时沿用 ASP.NET Core 校验问题的惯用标题
                return new ProblemDetailsActionResult(ExceptionProblemDetails.Create(
                    context.HttpContext, descriptor, localizer, titleFallback: "One or more validation errors occurred."));
            };
        });

        return builder;
    }

    /// <summary>注册全局异常处理器：绑定配置节，再应用宿主的编程式配置（代码覆盖配置文件）。</summary>
    /// <remarks>
    /// 可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加；
    /// 换用另一配置节时两个配置节都会绑定（后绑定的覆盖同名键）。
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">编程式配置（如错误码映射），在配置节绑定之后应用。</param>
    /// <param name="configSectionPath">配置节路径，默认 <c>Leistd:GlobalException</c>。</param>
    /// <example>
    /// <code>
    /// builder.Services.AddGlobalExceptionHandler(options =&gt; options.MapCode("Order:NotFound", 404));
    /// builder.Services.AddControllers().ConfigureApiValidation();
    ///
    /// app.UseGlobalExceptionHandler();   // 置于管道靠前位置
    ///
    /// // 业务层只提供稳定错误码与安全默认文案
    /// throw new BusinessException("Order:NotFound", "The order was not found.");
    /// </code>
    /// </example>
    public static IServiceCollection AddGlobalExceptionHandler(
        this IServiceCollection services,
        Action<GlobalExceptionOptions>? configure = null,
        string configSectionPath = GlobalExceptionOptions.SectionName)
    {
        services.AddProblemDetails();
        RegisterProblemDetailsConventions(services);

        var options = services.AddOptions<GlobalExceptionOptions>().BindConfiguration(configSectionPath);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, BusinessExceptionHandler>());

        return services;
    }

    /// <summary>把全局异常处理接入请求管道。应尽量靠近管道前端，以覆盖后续中间件抛出的异常。</summary>
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            AllowStatusCode404Response = true,
            ExceptionHandler = null,
            // 处理器放行的异常由本中间件经 IProblemDetailsService 写出，状态码取自这里。
            // .NET 10 的默认值一律 500；框架判定的请求错误（BadHttpRequestException）自带状态码，沿用它
            // （ASP.NET Core 主干已内置同一规则），与生产环境不抛异常、由状态码页写出的 400 同形。
            StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError,
            // 官方默认：处理器成功处理的异常由处理器按状态记一次日志，不再产生中间件的 Error 日志与
            // HandledException 诊断事件；未处理、处理失败的仍保留官方诊断。设置回调即完全取代默认，故须写回这一条。
            // 客户端主动断开由官方中间件在调用处理器与本回调之前直接以 499 返回（.NET 8+），这里无需再判。
            SuppressDiagnosticsCallback = ctx =>
                ctx.ExceptionHandledBy == ExceptionHandledType.ExceptionHandlerService
                // 处理器放行的框架请求错误是客户端问题，框架自己已记 Debug 日志
                || ctx.Exception is BadHttpRequestException
        });
    }

    private static void RegisterProblemDetailsConventions(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<ProblemDetailsOptions>,
            ProblemDetailsConventionsPostConfigure>());
    }

    // 所有写出的问题详情（异常、自动校验、状态码页、Results.Problem、框架各中间件）都经各写入器调用这里：
    // 在唯一的钩子上补齐本框架的约定，而不是为某一类失败另造一条写出路径。
    private sealed class ProblemDetailsConventionsPostConfigure : IPostConfigureOptions<ProblemDetailsOptions>
    {
        // ASP.NET Core 对 500 给的默认标题不是状态短语
        private const string DefaultServerErrorTitle = "An error occurred while processing your request.";

        public void PostConfigure(string? name, ProblemDetailsOptions options)
        {
            var customize = options.CustomizeProblemDetails;
            options.CustomizeProblemDetails = context =>
            {
                customize?.Invoke(context);
                var problem = context.ProblemDetails;
                // 与官方默认写入器同一取值（当前 Activity.Id，没有时为请求标识）：MVC 自动校验等路径不经默认写入器，
                // 这里统一补上，所有失败响应的 traceId 口径一致
                problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
                // 框架只给了状态码的问题详情，标题是英文默认值：按 Title:{状态码} 本地化。
                // 调用方自己写的标题不动。
                var status = problem.Status ?? context.HttpContext.Response.StatusCode;
                if (problem.Title is null
                    || problem.Title == ReasonPhrases.GetReasonPhrase(status)
                    || problem.Title == DefaultServerErrorTitle)
                {
                    problem.Title = ProblemTitles.Localize(
                        context.HttpContext.RequestServices.GetService<IStringLocalizer>(), status);
                }
            };
        }
    }

    // 自动模型校验的响应经统一管道写出；MVC 的 ObjectResult 走输出格式化器，不经过 IProblemDetailsService，
    // 用它会让启用信封的宿主在 400 上冒出一份 Problem Details。
    private sealed class ProblemDetailsActionResult(ProblemDetails problem) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            var httpContext = context.HttpContext;
            httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status400BadRequest;
            return httpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                .WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem })
                .AsTask();
        }
    }

    private sealed class ApiValidationRegistrationMarker { }
}
