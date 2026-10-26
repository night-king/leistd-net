using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CompanyName.ProjectName.Api.Filters;

/// <summary>给成功的对象结果加强 ETag；GET 请求的 <c>If-None-Match</c> 命中时改回 304，不发响应体。</summary>
/// <remarks>
/// ETag 取响应体 JSON 的摘要：同一个表示在不同端点（写入后的返回与机器读取）得到同一个 ETag，调用方据此做条件重验证。
/// 序列化用宿主的 MVC JSON 配置，与实际响应体一致；忽略 null 的规则让「未设置」与其他取值天然区分。
/// <c>If-None-Match</c> 按 RFC 9110 §13.1.2 解析：一个头里可以列多个标签，按弱比较匹配（<c>W/</c> 前缀不影响命中），<c>*</c> 匹配任何现有表示。
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StrongETagAttribute : ResultFilterAttribute
{
    public override async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: { } value } result && (result.StatusCode ?? StatusCodes.Status200OK) == StatusCodes.Status200OK)
        {
            var http = context.HttpContext;
            var serializerOptions = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
            var body = JsonSerializer.SerializeToUtf8Bytes(value, result.DeclaredType ?? value.GetType(), serializerOptions);
            var etag = new EntityTagHeaderValue($"\"{Base64Url.EncodeToString(SHA256.HashData(body).AsSpan(0, 16))}\"");
            http.Response.GetTypedHeaders().ETag = etag;

            if (HttpMethods.IsGet(http.Request.Method)
                && http.Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } candidates
                && candidates.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: false)))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            }
        }

        await next();
    }
}
