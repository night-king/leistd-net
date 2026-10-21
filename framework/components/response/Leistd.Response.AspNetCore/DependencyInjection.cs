using Leistd.Response.AspNetCore.Filters;
using Leistd.Response.AspNetCore.Writers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.Response.AspNetCore;

/// <summary>统一响应包装的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>在宿主的 MVC 链上挂载统一响应包装过滤器。</summary>
    /// <remarks>可重复调用：响应包装过滤器与问题详情写入器都只挂一份。</remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddControllers()
    ///     .AddJsonOptions(options =&gt; { /* 宿主自己的序列化配置 */ })
    ///     .AddResponseWrapper();
    ///
    /// // 控制器照常返回业务对象，过滤器自动包成 Result&lt;object?&gt;
    /// [HttpGet("{id}")]
    /// public async Task&lt;IActionResult&gt; GetAsync(long id) =&gt; Ok(await service.GetAsync(id));
    /// </code>
    /// </example>
    public static IMvcBuilder AddResponseWrapper(this IMvcBuilder builder)
    {
        // 自动模型校验经问题详情管道写出，信封写入器才接得到它
        Leistd.ExceptionHandling.AspNetCore.DependencyInjection.ConfigureApiValidation(builder);

        // 问题详情写入器按注册顺序先到先写：插到最前，才不被默认写入器与 MVC 的写入器抢先，
        // 且与宿主调用 AddProblemDetails / AddControllers 的先后无关
        if (!builder.Services.Any(service => service.ServiceType == typeof(IProblemDetailsWriter)
                && service.ImplementationType == typeof(ResultProblemDetailsWriter)))
        {
            builder.Services.Insert(0, ServiceDescriptor.Singleton<IProblemDetailsWriter, ResultProblemDetailsWriter>());
        }

        builder.AddMvcOptions(options =>
        {
            // 幂等：挂两遍会把响应包两层信封
            if (options.Filters.OfType<TypeFilterAttribute>()
                .Any(f => f.ImplementationType == typeof(ResultWrapperFilter)))
            {
                return;
            }

            options.Filters.Add<ResultWrapperFilter>();
        });

        return builder;
    }
}
