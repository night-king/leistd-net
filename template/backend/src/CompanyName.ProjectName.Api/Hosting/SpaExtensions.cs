using Microsoft.Net.Http.Headers;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>SPA 的静态文件缓存与静态回退。</summary>
/// <remarks>
/// 只服务部署形态（前端构建产物放在 wwwroot，由本服务同源托管）。本机开发时浏览器访问前端开发服务器，
/// 由它按 proxy.conf.mjs 把 API 与 Hub 请求转发过来，本服务不托管前端。
/// </remarks>
public static class SpaExtensions
{
    private const string FingerprintedCacheControl = "public, max-age=31536000, immutable";
    private const string RevalidateCacheControl = "no-cache";

    /// <summary>前端静态文件的托管选项：按文件是否带内容哈希设置 Cache-Control。</summary>
    /// <remarks>
    /// <para>不设 Cache-Control 时，浏览器按 Last-Modified 自行推算新鲜期，发版后会继续用旧的
    /// index.html 与词条文件，直到推算的期限过去。</para>
    /// <para>按目录约定判断，不按文件名猜哈希（Angular 的哈希长度与字符集并不固定）：
    /// 构建产物（根目录的 .js/.css 与 media/）在 outputHashing=all 下文件名随内容变化，缓存一年；
    /// index.html 与 public/ 下原样拷贝的文件（i18n/、images/、favicon 等）文件名不变，
    /// 用 no-cache 每次校验，未变化时只回 304。所以 public/ 根目录不要放 .js/.css。</para>
    /// </remarks>
    public static StaticFileOptions CreateSpaStaticFileOptions() => new()
    {
        OnPrepareResponse = context =>
            context.Context.Response.Headers[HeaderNames.CacheControl] =
                GetSpaCacheControl(context.Context.Request.Path, context.File.Name)
    };

    private static string GetSpaCacheControl(PathString requestPath, string fileName)
    {
        // SPA 回退时请求路径是前端路由，实际发出的是 index.html
        if (string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            return RevalidateCacheControl;
        }

        if (requestPath.StartsWithSegments("/media"))
        {
            return FingerprintedCacheControl;
        }

        var path = requestPath.Value ?? string.Empty;
        var isRootLevel = path.LastIndexOf('/') == 0;
        var isBundle = path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
        return isRootLevel && isBundle ? FingerprintedCacheControl : RevalidateCacheControl;
    }

    public static IEndpointRouteBuilder MapMyProjectSpaFallback(this WebApplication app)
    {
        app.Map("api", ReturnApiNotFoundAsync).WithDisplayName("ApiNotFoundFallback");
        app.Map("api/{**path}", ReturnApiNotFoundAsync).WithDisplayName("ApiNotFoundFallback");
        app.MapFallbackToFile("index.html", CreateSpaStaticFileOptions());

        return app;
    }

    private static Task ReturnApiNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
