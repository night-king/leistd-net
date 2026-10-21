using Microsoft.Extensions.Configuration;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>转发头信任配置的启动契约：写错的地址/网段必须让宿主起不来，且错误里带着键名与值。</summary>
/// <remarks>
/// <para>这段配置无条件存在（与多租户是否启用无关），因此测试也放在无条件生成的类里。</para>
/// <para>只验"合法值被采信"是不够的：<c>Configure&lt;ForwardedHeadersOptions&gt;</c> 是延迟回调，
/// 编译通过不能证明异常真的发生在启动阶段。忽略非法值在安全上是 fail-closed，
/// 但表现为网关后的 <c>X-Forwarded-*</c> 全部失效——HTTPS 重定向、OAuth 回调、子域租户解析
/// 一起坏，而配置看起来是对的。这类错误必须大声失败。</para>
/// </remarks>
public sealed class ForwardedHeadersConfigurationTests
{
    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "not-a-cidr")]
    public void Invalid_trusted_proxy_configuration_stops_the_host(string key, string value)
    {
        using var factory = new ProjectWebApplicationFactory();

        var error = Record.Exception(() =>
        {
            using var broken = factory.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [key] = value
                    })));

            // 触发宿主构建：延迟回调若从未执行，这里就不会抛
            using var client = ProjectWebApplicationFactory.CreateProjectClient(broken);
            _ = broken.Services;
        });

        Assert.NotNull(error);

        var message = Flatten(error);
        Assert.Contains(key.Split(':')[1], message, StringComparison.Ordinal);
        Assert.Contains(value, message, StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" | ", parts);
    }
}
