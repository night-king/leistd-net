#if (OpenIddictServer)
using System.Net;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>
/// 授权与退出请求启用了 OpenIddict 请求缓存：首个请求被存为 request token，
/// 重定向回同一端点（只带 client_id 与 request_uri）后才进入控制器。这里跟随这一跳，返回控制器给出的响应。
/// </summary>
internal static class OidcRequestCaching
{
    public static async Task<HttpResponseMessage> GetCachedAsync(this HttpClient client, string url) =>
        await FollowAsync(client, await client.GetAsync(url));

    public static async Task<HttpResponseMessage> PostCachedAsync(this HttpClient client, string url, HttpContent content) =>
        await FollowAsync(client, await client.PostAsync(url, content));

    /// <summary>首个请求的重定向目标：同一端点、带 request_uri 的相对地址。</summary>
    public static string CachedLocation(HttpClient client, HttpResponseMessage cached, string endpoint)
    {
        Assert.Equal(HttpStatusCode.Found, cached.StatusCode);
        var location = new Uri(client.BaseAddress!, cached.Headers.Location!);
        Assert.Equal(endpoint, location.AbsolutePath);
        Assert.Contains("request_uri=", location.Query);
        return location.PathAndQuery;
    }

    private static async Task<HttpResponseMessage> FollowAsync(HttpClient client, HttpResponseMessage cached)
    {
        using (cached)
        {
            return await client.GetAsync(CachedLocation(client, cached, cached.RequestMessage!.RequestUri!.AbsolutePath));
        }
    }
}
#endif
