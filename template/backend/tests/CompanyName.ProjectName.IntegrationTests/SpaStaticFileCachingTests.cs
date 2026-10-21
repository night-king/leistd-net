using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>前端静态文件的缓存头：带内容哈希的构建产物长期缓存，其余每次校验。</summary>
/// <remarks>
/// 缺了 no-cache，发版后浏览器会继续用旧的 index.html 与词条文件；
/// 反过来把不带哈希的文件标成 immutable，更新后一年内都取不到新内容。两头都不报错，只能在这里钉住。
/// </remarks>
public sealed class SpaStaticFileCachingTests : IDisposable
{
    private const string Fingerprinted = "public, max-age=31536000, immutable";
    private const string Revalidate = "no-cache";

    // 文件名取自真实构建产物：哈希的长度与字符集并不统一
    private static readonly string[] WebRootFiles =
    [
        "index.html",
        "main-Q7K2WI64.js",
        "chunk-B6s-Xnrj.js",
        "styles-BTJSTJZA.css",
        "media/geist-latin-400-normal-3YTLMAJE.woff2",
        "i18n/en.json",
        "images/og-default.png",
        "favicon.svg",
    ];

    private readonly DirectoryInfo webRoot = Directory.CreateTempSubdirectory("spa-cache-");

    [Fact]
    public async Task Fingerprinted_bundles_are_cached_and_everything_else_is_revalidated()
    {
        foreach (var file in WebRootFiles)
        {
            var path = Path.Combine(webRoot.FullName, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file);
        }

        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.UseSetting(WebHostDefaults.WebRootKey, webRoot.FullName));
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);

        (string Path, string Expected)[] cases =
        [
            ("/", Revalidate),
            ("/index.html", Revalidate),
            // SPA 回退：请求的是前端路由，发出的是 index.html
            ("/workspace/dashboard", Revalidate),
            ("/i18n/en.json", Revalidate),
            ("/images/og-default.png", Revalidate),
            ("/favicon.svg", Revalidate),
            ("/main-Q7K2WI64.js", Fingerprinted),
            ("/chunk-B6s-Xnrj.js", Fingerprinted),
            ("/styles-BTJSTJZA.css", Fingerprinted),
            ("/media/geist-latin-400-normal-3YTLMAJE.woff2", Fingerprinted),
        ];

        var mismatches = new List<string>();
        foreach (var (path, expected) in cases)
        {
            var response = await client.GetAsync(path);
            var actual = response.Headers.CacheControl?.ToString();
            if (response.StatusCode != HttpStatusCode.OK || actual != expected)
            {
                mismatches.Add($"{path}: {(int)response.StatusCode} '{actual}'，期望 200 '{expected}'");
            }
        }

        Assert.Empty(mismatches);
    }

    public void Dispose() => webRoot.Delete(recursive: true);
}
