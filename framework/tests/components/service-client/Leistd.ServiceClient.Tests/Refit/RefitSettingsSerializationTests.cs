using System.Net.Http.Headers;
using System.Text;
using Leistd.ServiceClient.Refit.Options;
using Refit;
using Xunit;

namespace Leistd.ServiceClient.Tests.Refit;

/// <summary>框架统一 <see cref="RefitSettings"/> 的序列化契约。</summary>
/// <remarks>
/// <para>这里钉的是<b>跨服务契约</b>：调用方与被调方各自独立配置序列化，两侧不一致时只有真实
/// 跨服务调用才会暴露——而那种调用在单服务集成测试与模板矩阵里都不发生（假客户端替掉了这一层）。
/// 因此形态必须在框架层被断言。</para>
/// <para>断言走<b>真实的</b> <c>ContentSerializer</c>，不另建一份 options 副本：
/// 副本会随源码漂移，测试照样绿。</para>
/// </remarks>
public class RefitSettingsSerializationTests
{
    public enum SampleMode
    {
        SharedDatabase,
        DedicatedDatabase
    }

    private sealed record SamplePayload
    {
        public required SampleMode DatabaseMode { get; init; }
    }

    private static IHttpContentSerializer Serializer() =>
        ServiceClientRefitSettings.Create().ContentSerializer;

    private static HttpContent JsonContent(string json) =>
        new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    /// <summary>
    /// 字符串枚举必须能读。<c>JsonSerializerDefaults.Web</c> 不带枚举转换器，
    /// 少了它枚举只接受数字，而被调方给的是字符串。
    /// </summary>
    [Theory]
    [InlineData("sharedDatabase", SampleMode.SharedDatabase)]
    [InlineData("dedicatedDatabase", SampleMode.DedicatedDatabase)]
    // 被调方用 PascalCase 也要能读：两侧命名策略不同不该炸
    [InlineData("SharedDatabase", SampleMode.SharedDatabase)]
    [InlineData("DedicatedDatabase", SampleMode.DedicatedDatabase)]
    public async Task Reads_string_enum_values(string wireValue, SampleMode expected)
    {
        var payload = await Serializer().FromHttpContentAsync<SamplePayload>(
            JsonContent($"{{\"databaseMode\":\"{wireValue}\"}}"));

        Assert.NotNull(payload);
        Assert.Equal(expected, payload.DatabaseMode);
    }

    /// <summary>数字取值也接受：调用方要能读被调方给什么就读什么</summary>
    [Fact]
    public async Task Reads_numeric_enum_values()
    {
        var payload = await Serializer().FromHttpContentAsync<SamplePayload>(
            JsonContent("""{"databaseMode":1}"""));

        Assert.NotNull(payload);
        Assert.Equal(SampleMode.DedicatedDatabase, payload.DatabaseMode);
    }

    /// <summary>出站也写字符串而非数字，且与属性同为 camelCase</summary>
    [Fact]
    public async Task Writes_camel_case_string_enum_values()
    {
        var content = Serializer().ToHttpContent(
            new SamplePayload { DatabaseMode = SampleMode.DedicatedDatabase });

        var json = await content.ReadAsStringAsync();

        Assert.Contains("\"databaseMode\":\"dedicatedDatabase\"", json, StringComparison.Ordinal);
    }
}
